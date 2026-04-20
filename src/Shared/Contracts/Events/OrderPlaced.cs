namespace Shared.Contracts.Events;

public record OrderPlaced(
    Guid CorrelationId,
    Guid OrderId,
    string CustomerId,
    decimal TotalAmount,
    IReadOnlyList<OrderItem> Items,
    DateTime PlacedAt,
    string? SimulateFailure = null
);
