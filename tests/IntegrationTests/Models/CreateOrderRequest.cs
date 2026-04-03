namespace IntegrationTests.Models;

public record CreateOrderRequest(
    decimal TotalAmount,
    List<OrderItemRequest> Items
);

public record OrderItemRequest(
    string ProductId,
    int Quantity,
    decimal UnitPrice
);
