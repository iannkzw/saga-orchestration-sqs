namespace Shared.Contracts.Commands;

public record CancelPayment : BaseCommand
{
    public Guid CorrelationId { get; init; }
    public string PaymentId { get; init; } = string.Empty;
}
