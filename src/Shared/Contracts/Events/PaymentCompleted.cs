namespace Shared.Contracts.Events;

public record PaymentCompleted(
    Guid CorrelationId,
    string PaymentId,
    DateTime ProcessedAt
);
