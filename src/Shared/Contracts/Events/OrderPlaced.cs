namespace Shared.Contracts.Events;

public record OrderPlaced(
    Guid CorrelationId,
    string CustomerId,
    decimal TotalAmount,
    IReadOnlyList<OrderItem> Items,
    DateTime PlacedAt
);
