namespace IntegrationTests.Models;

public record OrderResponse(
    Guid OrderId,
    Guid? SagaId,
    string Status,
    decimal TotalAmount
);
