namespace Shared.Contracts.Events;

public record ShippingFailed(
    Guid CorrelationId,
    string Reason
);
