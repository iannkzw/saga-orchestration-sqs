namespace Shared.Contracts.Replies;

public record RefundPaymentReply : BaseReply
{
    public string? RefundId { get; init; }
}
