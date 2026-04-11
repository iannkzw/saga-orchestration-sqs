namespace Shared.Contracts.Events;

public record InventoryReserved(
    Guid CorrelationId,
    string ReservationId,
    DateTime ReservedAt
);
