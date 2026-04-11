namespace Shared.Contracts.Commands;

using Shared.Contracts.Events;

public record CreateOrder : BaseCommand
{
    public Guid OrderId { get; init; }
    public decimal TotalAmount { get; init; }
    public List<OrderItem> Items { get; init; } = [];
}
