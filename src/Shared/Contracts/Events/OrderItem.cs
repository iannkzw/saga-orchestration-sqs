namespace Shared.Contracts.Events;

public record OrderItem(
    string ProductId,
    int Quantity,
    decimal UnitPrice
);
