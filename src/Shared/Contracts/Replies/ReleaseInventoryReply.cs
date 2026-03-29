namespace Shared.Contracts.Replies;

public record ReleaseInventoryReply : BaseReply
{
    public string? ReservationId { get; init; }
}
