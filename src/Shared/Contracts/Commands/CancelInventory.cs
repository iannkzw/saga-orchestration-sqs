namespace Shared.Contracts.Commands;

public record CancelInventory : BaseCommand
{
    public Guid CorrelationId { get; init; }
    public string ReservationId { get; init; } = string.Empty;
}
