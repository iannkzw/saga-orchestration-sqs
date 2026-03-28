namespace Shared.Contracts.Replies;

public record InventoryReply : BaseReply
{
    public string? ReservationId { get; init; }
}
