namespace Shared.Contracts.Commands;

public record ProcessPayment : BaseCommand
{
    public Guid OrderId { get; init; }
    public decimal Amount { get; init; }
}
