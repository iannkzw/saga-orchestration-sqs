namespace Shared.Contracts.Commands;

public record CancelPayment(
    Guid CorrelationId,
    string PaymentId
);
