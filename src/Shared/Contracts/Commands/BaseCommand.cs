namespace Shared.Contracts.Commands;

public abstract record BaseCommand
{
    public Guid SagaId { get; init; }
    public string IdempotencyKey { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
