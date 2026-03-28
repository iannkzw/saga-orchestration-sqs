namespace Shared.Contracts.Replies;

public record ShippingReply : BaseReply
{
    public string? TrackingNumber { get; init; }
}
