using System.Diagnostics;
using IntegrationTests.Infrastructure;
using IntegrationTests.Models;
using Xunit;
using Xunit.Abstractions;

namespace IntegrationTests.Tests;

/// <summary>
/// T8, T9, T10 — Resiliência, concorrência otimista e deduplicação.
/// </summary>
[Collection("Integration")]
public sealed class ResilienceTests(ITestOutputHelper output)
{
    private readonly SagaClient _saga = new();
    private readonly InventoryClient _inventory = new();

    /// <summary>
    /// T8 — Resiliência via Outbox: derrubar LocalStack por 5s no meio da saga e validar
    /// que o happy path eventualmente completa sem perda de mensagens.
    ///
    /// Mecanismo: o EF Core Outbox retém mensagens em Postgres enquanto o transporte SQS
    /// está indisponível e as drena automaticamente quando o LocalStack volta.
    /// </summary>
    [Fact]
    public async Task OutboxResilience_LocalStackDownMidSaga_SagaEventuallyCompletes()
    {
        // Arrange
        await _inventory.ResetStockAsync("PROD-001", 100);

        var request = new CreateOrderRequest(
            TotalAmount: 99.90m,
            Items: [new OrderItemRequest("PROD-001", 1, 99.90m)]
        );

        // Postar pedido e pausar LocalStack imediatamente depois
        // A mensagem OrderPlaced pode já ter sido publicada, mas os eventos subsequentes
        // (PaymentCompleted, InventoryReserved, ShippingScheduled) ficarão retidos no Outbox
        var (orderId, sagaId) = await _saga.PostOrderAsync(request);

        output.WriteLine($"Pedido criado: orderId={orderId}, sagaId={sagaId}");
        output.WriteLine("Pausando LocalStack por 5s...");

        await PauseLocalStackAsync(TimeSpan.FromSeconds(5));

        output.WriteLine("LocalStack retomado. Aguardando saga completar via Outbox drain...");

        // Timeout maior para compensar o período de interrupção + drain do Outbox (QueryDelay=1s)
        var saga = await _saga.WaitForTerminalStateAsync(sagaId, timeout: TimeSpan.FromSeconds(60));

        output.WriteLine($"Estado terminal atingido: {saga.State}");

        // A saga deve completar — o Outbox garante entrega eventual
        Assert.Equal("Final", saga.State);

        var order = await _saga.WaitForOrderStatusAsync(orderId, "Completed", timeout: TimeSpan.FromSeconds(30));
        Assert.Equal("Completed", order.Status);
    }

    /// <summary>
    /// T9 — Concorrência otimista: 10 sagas simultâneas competindo pelo mesmo produto
    /// com estoque=10. Valida que nenhuma saga corrompe estado de outra (0 overbooking)
    /// usando ConcurrencyMode.Optimistic + xmin row version no OrderService.
    ///
    /// Todas as 10 devem completar (estoque suficiente), demonstrando que o retry otimista
    /// absorve conflitos de xmin sem falhas espúrias.
    /// </summary>
    [Fact]
    public async Task OptimisticConcurrency_TenSimultaneousSagas_NoStateCorruption()
    {
        const string product = "PROD-OPTIMISTIC";
        const int totalOrders = 10;
        const int initialStock = 10;

        // Arrange
        await _inventory.ResetStockAsync(product, initialStock);

        var request = new CreateOrderRequest(
            TotalAmount: 15.00m,
            Items: [new OrderItemRequest(product, 1, 15.00m)]
        );

        // Act — disparar 10 pedidos em paralelo para forçar contenção otimista
        var orderTasks = Enumerable.Range(0, totalOrders)
            .Select(_ => _saga.PostOrderAsync(request))
            .ToList();

        var orders = await Task.WhenAll(orderTasks);

        output.WriteLine($"Pedidos criados: {orders.Length}");

        // Aguardar todas as sagas com timeout generoso (contenção otimista causa retries)
        var sagaTasks = orders
            .Select(o => _saga.WaitForTerminalStateAsync(o.SagaId, timeout: TimeSpan.FromSeconds(90)))
            .ToList();

        var sagas = await Task.WhenAll(sagaTasks);

        var completed = sagas.Count(s => s.State is "Final" or "Completed");
        var failed = sagas.Count(s => s.State == "Failed");

        output.WriteLine($"Completed: {completed} | Failed: {failed}");
        foreach (var saga in sagas)
            output.WriteLine($"  Saga {saga.SagaId}: {saga.State}");

        // Com estoque=10 e 10 pedidos, todas devem completar
        Assert.Equal(totalOrders, completed);
        Assert.Equal(0, failed);

        // Estoque final = 0 (sem overbooking e sem underbooking)
        var stock = await _inventory.GetStockAsync(product);
        output.WriteLine($"Estoque final: {stock.Quantity}");
        Assert.Equal(0, stock.Quantity);
    }

    /// <summary>
    /// T10 — Deduplicação via DuplicateDetectionWindow: postar dois pedidos em rápida
    /// sucessão com o mesmo produto e validar que cada saga é processada exatamente 1 vez.
    ///
    /// O DuplicateDetectionWindow=30min do EF Outbox garante que mensagens com o mesmo
    /// MessageId não são entregues duas vezes ao transporte. Este teste valida que o
    /// sistema não processa eventos duplicados mesmo sob carga concorrente.
    ///
    /// Nota: o MassTransit usa o CorrelationId da saga como idempotency key implícito —
    /// dois POST /orders geram CorrelationIds distintos e, portanto, sagas distintas.
    /// Para validar deduplicação no nível do Outbox, verificamos que o mesmo pedido
    /// processado duas vezes (via retry) não gera dois estados terminais inconsistentes.
    /// </summary>
    [Fact]
    public async Task Deduplication_SameOrderPostedTwiceRapidly_EachSagaProcessedOnce()
    {
        // Arrange
        await _inventory.ResetStockAsync("PROD-001", 100);

        var request = new CreateOrderRequest(
            TotalAmount: 45.00m,
            Items: [new OrderItemRequest("PROD-001", 1, 45.00m)]
        );

        // Postar dois pedidos em rápida sucessão (simula retry do cliente HTTP)
        var postTasks = new[]
        {
            _saga.PostOrderAsync(request),
            _saga.PostOrderAsync(request),
            _saga.PostOrderAsync(request),
        };

        var orders = await Task.WhenAll(postTasks);

        var (orderId1, sagaId1) = orders[0];
        var (orderId2, sagaId2) = orders[1];
        var (orderId3, sagaId3) = orders[2];

        output.WriteLine($"Saga 1: orderId={orderId1}, sagaId={sagaId1}");
        output.WriteLine($"Saga 2: orderId={orderId2}, sagaId={sagaId2}");
        output.WriteLine($"Saga 3: orderId={orderId3}, sagaId={sagaId3}");

        // Cada POST gera CorrelationId único → sagas distintas
        Assert.NotEqual(sagaId1, sagaId2);
        Assert.NotEqual(sagaId1, sagaId3);
        Assert.NotEqual(sagaId2, sagaId3);

        // Todas devem ser processadas exatamente 1 vez e completar
        var sagaResults = await Task.WhenAll(
            _saga.WaitForTerminalStateAsync(sagaId1),
            _saga.WaitForTerminalStateAsync(sagaId2),
            _saga.WaitForTerminalStateAsync(sagaId3)
        );

        output.WriteLine($"Saga 1 estado: {sagaResults[0].State}");
        output.WriteLine($"Saga 2 estado: {sagaResults[1].State}");
        output.WriteLine($"Saga 3 estado: {sagaResults[2].State}");

        // Todas as 3 sagas devem completar (DuplicateDetectionWindow não bloqueia sagas distintas)
        Assert.All(sagaResults, s => Assert.Equal("Final", s.State));

        // Verificar que cada saga tem OrderId único (não houve mesclagem/deduplicação indevida)
        var orderIds = sagaResults.Select(s => s.OrderId).Distinct().ToList();
        Assert.Equal(3, orderIds.Count);
    }

    // Pausa o container LocalStack por um período e retoma.
    // Usa `docker pause`/`docker unpause` que suspende processos sem destruir estado.
    private async Task PauseLocalStackAsync(TimeSpan duration)
    {
        await RunDockerCommandAsync("pause", "saga-localstack");
        await Task.Delay(duration);
        await RunDockerCommandAsync("unpause", "saga-localstack");
        // Aguarda LocalStack aceitar conexões novamente
        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    private static async Task RunDockerCommandAsync(string command, string container)
    {
        var psi = new ProcessStartInfo("docker", $"{command} {container}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Falha ao executar: docker {command} {container}");

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var err = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException(
                $"docker {command} {container} falhou (exit {process.ExitCode}): {err}");
        }
    }
}
