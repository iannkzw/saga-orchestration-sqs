using IntegrationTests.Infrastructure;
using IntegrationTests.Models;
using Xunit;

namespace IntegrationTests.Tests;

/// <summary>
/// T1 — Happy path: pedido válido → saga atinge Completed com todas as transições esperadas.
/// </summary>
[Collection("Integration")]
public sealed class HappyPathTests
{
    private readonly SagaClient _saga = new();
    private readonly InventoryClient _inventory = new();

    [Fact]
    public async Task PostOrder_ValidProduct_SagaCompletes()
    {
        // Arrange
        await _inventory.ResetStockAsync("PROD-001", 100);

        var request = new CreateOrderRequest(
            TotalAmount: 99.90m,
            Items: [new OrderItemRequest("PROD-001", 1, 99.90m)]
        );

        // Act
        var (orderId, sagaId) = await _saga.PostOrderAsync(request);
        var saga = await _saga.WaitForTerminalStateAsync(sagaId);

        // Assert — estado terminal
        Assert.Equal("Final", saga.State);

        // Assert — IDs corretos
        Assert.NotEqual(Guid.Empty, orderId);
        Assert.NotEqual(Guid.Empty, sagaId);

        // Assert — order.status reflete estado terminal
        var order = await _saga.WaitForOrderStatusAsync(orderId, "Completed");
        Assert.Equal("Completed", order.Status);
    }
}
