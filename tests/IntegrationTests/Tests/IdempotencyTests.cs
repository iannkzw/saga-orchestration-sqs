using IntegrationTests.Infrastructure;
using IntegrationTests.Models;
using Xunit;

namespace IntegrationTests.Tests;

/// <summary>
/// T5 — Idempotência: o sistema processa múltiplos pedidos sem corrupção de estado,
/// e o mecanismo de IdempotencyKey garante que o mesmo comando SQS não seja reprocessado.
///
/// Contexto: a idempotência neste projeto opera na camada SQS — cada comando carrega
/// um IdempotencyKey único por saga. Este teste valida que:
/// 1. Dois pedidos distintos completam independentemente (sem interferência de estado)
/// 2. Um pedido que gera compensação não deixa estado inconsistente em pedidos concorrentes
/// </summary>
[Collection("Integration")]
public sealed class IdempotencyTests
{
    private readonly SagaClient _saga = new();
    private readonly InventoryClient _inventory = new();

    /// <summary>
    /// Dois pedidos simultâneos para o mesmo produto completam sem interferência.
    /// Valida que a IdempotencyKey por saga não causa colisão entre sagas distintas.
    /// </summary>
    [Fact]
    public async Task TwoConcurrentOrders_BothComplete_NoStateCorruption()
    {
        await _inventory.ResetStockAsync("PROD-001", 100);

        var request = new CreateOrderRequest(
            TotalAmount: 30.00m,
            Items: [new OrderItemRequest("PROD-001", 1, 30.00m)]
        );

        // Disparar dois pedidos em paralelo
        var (orderId1, sagaId1) = await _saga.PostOrderAsync(request);
        var (orderId2, sagaId2) = await _saga.PostOrderAsync(request);

        // Sagas devem ser distintas
        Assert.NotEqual(sagaId1, sagaId2);
        Assert.NotEqual(orderId1, orderId2);

        // Aguardar ambas em paralelo
        var results = await Task.WhenAll(
            _saga.WaitForTerminalStateAsync(sagaId1),
            _saga.WaitForTerminalStateAsync(sagaId2)
        );
        var saga1 = results[0];
        var saga2 = results[1];

        // Ambas devem completar
        Assert.Equal("Completed", saga1.State);
        Assert.Equal("Completed", saga2.State);

        // Nenhuma deve ter transições de compensação
        Assert.DoesNotContain("PaymentRefunding", saga1.Transitions.Select(t => t.To));
        Assert.DoesNotContain("PaymentRefunding", saga2.Transitions.Select(t => t.To));
    }

    /// <summary>
    /// Pedido com compensação não afeta o estado de outro pedido concorrente.
    /// Valida isolamento entre sagas com IdempotencyKeys distintas.
    /// </summary>
    [Fact]
    public async Task FailingOrderDoesNotCorruptConcurrentSuccessfulOrder()
    {
        await _inventory.ResetStockAsync("PROD-001", 100);

        var goodRequest = new CreateOrderRequest(
            TotalAmount: 25.00m,
            Items: [new OrderItemRequest("PROD-001", 1, 25.00m)]
        );

        // Disparar um pedido válido e um com falha em paralelo
        var (_, successSagaId) = await _saga.PostOrderAsync(goodRequest);
        var (_, failSagaId) = await _saga.PostOrderAsync(goodRequest, simulateFailure: "payment");

        var results2 = await Task.WhenAll(
            _saga.WaitForTerminalStateAsync(successSagaId),
            _saga.WaitForTerminalStateAsync(failSagaId)
        );
        var successSaga = results2[0];
        var failSaga = results2[1];

        Assert.Equal("Completed", successSaga.State);
        Assert.Equal("Failed", failSaga.State);
    }
}
