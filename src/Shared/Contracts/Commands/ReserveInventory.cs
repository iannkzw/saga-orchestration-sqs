namespace Shared.Contracts.Commands;

public record ReserveInventory : BaseCommand
{
    public Guid OrderId { get; init; }
    public List<InventoryItem> Items { get; init; } = [];
}

public record InventoryItem
{
    public string ProductId { get; init; } = string.Empty;
    public int Quantity { get; init; }
}
