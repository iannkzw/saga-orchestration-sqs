namespace Shared.Contracts.Events;

public record InventoryFailed(
    Guid CorrelationId,
    string Reason
);
