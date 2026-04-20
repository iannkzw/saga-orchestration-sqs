namespace Shared.Contracts.Commands;

using Shared.Contracts.Events;

public record ScheduleShipping : BaseCommand
{
    public Guid CorrelationId { get; init; }
    public Guid OrderId { get; init; }
    public IReadOnlyList<OrderItem> Items { get; init; } = [];
    public ShippingAddress? ShippingAddress { get; init; }
    public string? SimulateFailure { get; init; }
}
