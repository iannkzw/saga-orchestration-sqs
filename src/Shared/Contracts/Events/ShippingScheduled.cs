namespace Shared.Contracts.Events;

public record ShippingScheduled(
    Guid CorrelationId,
    string TrackingCode,
    DateTime ScheduledAt
);
