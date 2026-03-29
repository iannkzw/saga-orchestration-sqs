namespace Shared.Contracts.Replies;

public record CancelShippingReply : BaseReply
{
    public string? TrackingNumber { get; init; }
}
