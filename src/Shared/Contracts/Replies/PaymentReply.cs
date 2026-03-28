namespace Shared.Contracts.Replies;

public record PaymentReply : BaseReply
{
    public string? TransactionId { get; init; }
}
