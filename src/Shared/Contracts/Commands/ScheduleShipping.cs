namespace Shared.Contracts.Commands;

public record ScheduleShipping : BaseCommand
{
    public Guid OrderId { get; init; }
    public string ShippingAddress { get; init; } = string.Empty;
}
