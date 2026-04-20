namespace Shared.Contracts.Commands;

using Shared.Contracts.Events;

public record ReserveInventory : BaseCommand
{
    public Guid CorrelationId { get; init; }
    public Guid OrderId { get; init; }
    public IReadOnlyList<OrderItem> Items { get; init; } = [];
    public string? SimulateFailure { get; init; }
}
