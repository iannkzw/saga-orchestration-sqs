namespace Shared.Contracts.Commands;

public record CreateOrder : BaseCommand
{
    public Guid OrderId { get; init; }
    public decimal TotalAmount { get; init; }
    public List<OrderItem> Items { get; init; } = [];
}

public record OrderItem
{
    public string ProductId { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
}
