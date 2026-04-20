namespace Shared.Contracts.Commands;

public abstract record BaseCommand
{
    public Guid SagaId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
