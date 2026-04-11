namespace IntegrationTests.Models;

public record SagaResponse(
    Guid SagaId,
    Guid OrderId,
    string State,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record SagaTransition(
    string From,
    string To,
    string TriggeredBy,
    DateTime Timestamp
);
