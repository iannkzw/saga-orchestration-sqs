namespace Shared.Contracts.Events;

using Shared.Contracts.Commands;

public record OrderPlaced
{
    public Guid CorrelationId { get; init; }
    public Guid OrderId { get; init; }
    public decimal TotalAmount { get; init; }
    public List<OrderItem> Items { get; init; } = [];
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
