namespace Shared.Contracts.Commands;

public record RefundPayment : BaseCommand
{
    public Guid OrderId { get; init; }
    public decimal Amount { get; init; }
    public string TransactionId { get; init; } = string.Empty;
}
