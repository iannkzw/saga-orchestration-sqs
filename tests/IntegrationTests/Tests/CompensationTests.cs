using IntegrationTests.Infrastructure;
using IntegrationTests.Models;
using Xunit;

namespace IntegrationTests.Tests;

/// <summary>
/// T2, T3, T4 — Cenários de falha e compensação em cascata.
/// </summary>
[Collection("Integration")]
public sealed class CompensationTests
{
    private readonly SagaClient _saga = new();

    private static readonly CreateOrderRequest DefaultOrder = new(
        TotalAmount: 50.00m,
        Items: [new OrderItemRequest("PROD-001", 1, 50.00m)]
    );

    /// <summary>
    /// T2 — Falha no pagamento: saga termina em Failed sem nenhuma compensação
    /// (payment não completou, nada a compensar).
    /// </summary>
    [Fact]
    public async Task PaymentFailure_SagaFails_NoCompensation()
    {
        var (_, sagaId) = await _saga.PostOrderAsync(DefaultOrder, simulateFailure: "payment");
        var saga = await _saga.WaitForTerminalStateAsync(sagaId);

        Assert.Equal("Failed", saga.State);

        var toStates = saga.Transitions.Select(t => t.To).ToList();
        Assert.DoesNotContain("InventoryReleasing", toStates);
        Assert.DoesNotContain("PaymentRefunding", toStates);
    }

    /// <summary>
    /// T3 — Falha no inventário: payment já foi feito, compensação inclui PaymentRefunding.
    /// </summary>
    [Fact]
    public async Task InventoryFailure_SagaFails_PaymentRefunded()
    {
        var (_, sagaId) = await _saga.PostOrderAsync(DefaultOrder, simulateFailure: "inventory");
        var saga = await _saga.WaitForTerminalStateAsync(sagaId);

        Assert.Equal("Failed", saga.State);

        var toStates = saga.Transitions.Select(t => t.To).ToList();
        Assert.Contains("PaymentRefunding", toStates);
        Assert.DoesNotContain("InventoryReleasing", toStates);
    }

    /// <summary>
    /// T4 — Falha no shipping: payment e inventory foram feitos, compensação inclui ambos.
    /// </summary>
    [Fact]
    public async Task ShippingFailure_SagaFails_InventoryAndPaymentCompensated()
    {
        var (_, sagaId) = await _saga.PostOrderAsync(DefaultOrder, simulateFailure: "shipping");
        var saga = await _saga.WaitForTerminalStateAsync(sagaId);

        Assert.Equal("Failed", saga.State);

        var toStates = saga.Transitions.Select(t => t.To).ToList();
        Assert.Contains("InventoryReleasing", toStates);
        Assert.Contains("PaymentRefunding", toStates);
    }
}
