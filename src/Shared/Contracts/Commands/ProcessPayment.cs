namespace Shared.Contracts.Commands;

public record ProcessPayment : BaseCommand
{
    public Guid CorrelationId { get; init; }
    public Guid OrderId { get; init; }
    public decimal Amount { get; init; }
    public string CustomerId { get; init; } = string.Empty;
}
