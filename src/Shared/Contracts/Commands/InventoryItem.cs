namespace Shared.Contracts.Commands;

public record InventoryItem
{
    public string ProductId { get; init; } = string.Empty;
    public int Quantity { get; init; }
}
