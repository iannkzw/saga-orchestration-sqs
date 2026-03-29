namespace Shared.Contracts.Commands;

public record ReleaseInventory : BaseCommand
{
    public Guid OrderId { get; init; }
    public string ReservationId { get; init; } = string.Empty;
}
