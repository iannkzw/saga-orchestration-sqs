using IntegrationTests.Infrastructure;
using IntegrationTests.Models;
using Xunit;
using Xunit.Abstractions;

namespace IntegrationTests.Tests;

/// <summary>
/// T6, T7 — Concorrência: 5 pedidos simultâneos para um produto com estoque=2.
/// </summary>
[Collection("Integration")]
public sealed class ConcurrencyTests(ITestOutputHelper output)
{
    private const string ConcurrentProduct = "PROD-TEST-CONCURRENT";
    private const int TotalOrders = 5;
    private const int InitialStock = 2;

    private readonly SagaClient _saga = new();
    private readonly InventoryClient _inventory = new();

    /// <summary>
    /// T6 — Com pessimistic lock (INVENTORY_LOCKING_MODE=pessimistic):
    /// exatamente 2 sagas completam e 3 falham. Sem overbooking.
    /// </summary>
    [Fact]
    public async Task WithPessimisticLock_ExactlyTwoComplete_NoOverbooking()
    {
        // Arrange
        await _inventory.ResetStockAsync(ConcurrentProduct, InitialStock);

        var request = new CreateOrderRequest(
            TotalAmount: 10.00m,
            Items: [new OrderItemRequest(ConcurrentProduct, 1, 10.00m)]
        );

        // Act — disparar todos os pedidos em paralelo
        var orderTasks = Enumerable.Range(0, TotalOrders)
            .Select(_ => _saga.PostOrderAsync(request))
            .ToList();

        var orders = await Task.WhenAll(orderTasks);

        // Aguardar todas as sagas atingirem estado terminal (timeout generoso para concorrência)
        var sagaTasks = orders
            .Select(o => _saga.WaitForTerminalStateAsync(o.SagaId, timeout: TimeSpan.FromSeconds(60)))
            .ToList();

        var sagas = await Task.WhenAll(sagaTasks);

        // Assert
        var completed = sagas.Count(s => s.State == "Completed");
        var failed = sagas.Count(s => s.State == "Failed");

        output.WriteLine($"Completed: {completed}, Failed: {failed}");
        foreach (var saga in sagas)
        {
            output.WriteLine($"  Saga {saga.SagaId}: {saga.State}");
        }

        Assert.Equal(InitialStock, completed);
        Assert.Equal(TotalOrders - InitialStock, failed);

        // Estoque final deve ser 0 (nenhum overbooking)
        var stock = await _inventory.GetStockAsync(ConcurrentProduct);
        output.WriteLine($"Estoque final: {stock.Quantity}");
        Assert.Equal(0, stock.Quantity);
    }

    /// <summary>
    /// T7 — Documentacional: sem lock, overbooking pode ocorrer.
    /// Este teste SEMPRE passa — o objetivo é registrar o comportamento observado.
    ///
    /// Para demonstrar overbooking real, reinicie o inventory-service com
    /// INVENTORY_LOCKING_MODE=none antes de rodar este teste manualmente.
    ///
    /// No CI, o ambiente usa INVENTORY_LOCKING_MODE=pessimistic (docker-compose.test.yml),
    /// portanto o resultado será idêntico ao T6. Isso é esperado e intencional.
    /// </summary>
    [Fact]
    public async Task WithoutLock_DocumentsBehavior_AlwaysPasses()
    {
        // Arrange
        await _inventory.ResetStockAsync(ConcurrentProduct, InitialStock);

        var request = new CreateOrderRequest(
            TotalAmount: 10.00m,
            Items: [new OrderItemRequest(ConcurrentProduct, 1, 10.00m)]
        );

        // Act
        var orderTasks = Enumerable.Range(0, TotalOrders)
            .Select(_ => _saga.PostOrderAsync(request))
            .ToList();

        var orders = await Task.WhenAll(orderTasks);

        var sagaTasks = orders
            .Select(o => _saga.WaitForTerminalStateAsync(o.SagaId, timeout: TimeSpan.FromSeconds(60)))
            .ToList();

        var sagas = await Task.WhenAll(sagaTasks);

        var completed = sagas.Count(s => s.State == "Completed");
        var failed = sagas.Count(s => s.State == "Failed");
        var stock = await _inventory.GetStockAsync(ConcurrentProduct);

        // Registrar resultado observado
        output.WriteLine("=== COMPORTAMENTO SEM LOCK (documentacional) ===");
        output.WriteLine($"Pedidos: {TotalOrders} | Estoque inicial: {InitialStock}");
        output.WriteLine($"Completed: {completed} | Failed: {failed}");
        output.WriteLine($"Estoque final: {stock.Quantity}");

        bool overbooking = completed > InitialStock;
        output.WriteLine(overbooking
            ? $"⚠️  OVERBOOKING DETECTADO: {completed} sagas completaram para estoque de {InitialStock}"
            : $"✓ Sem overbooking (lock pode estar ativo ou sequencialização natural)");

        // Assert relaxada: pelo menos um pedido completou (sistema funcionou)
        Assert.True(completed >= 1, $"Nenhum pedido completou — verifique o ambiente. Completed={completed}");
        Assert.Equal(TotalOrders, completed + failed);
    }
}
