namespace Shared.Contracts.Commands;

public record CancelShipping : BaseCommand
{
    public Guid OrderId { get; init; }
    public string TrackingNumber { get; init; } = string.Empty;
}
