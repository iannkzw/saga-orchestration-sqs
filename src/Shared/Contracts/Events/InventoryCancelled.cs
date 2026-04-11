namespace Shared.Contracts.Events;

public record InventoryCancelled(
    Guid CorrelationId,
    string ReservationId
);
