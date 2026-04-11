namespace Shared.Contracts.Commands;

public record CancelInventory(
    Guid CorrelationId,
    string ReservationId
);
