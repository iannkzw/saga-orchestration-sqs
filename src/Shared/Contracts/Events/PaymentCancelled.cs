namespace Shared.Contracts.Events;

public record PaymentCancelled(
    Guid CorrelationId,
    string PaymentId
);
