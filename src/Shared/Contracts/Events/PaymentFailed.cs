namespace Shared.Contracts.Events;

public record PaymentFailed(
    Guid CorrelationId,
    string Reason
);
