namespace Shared.Contracts.Replies;

public abstract record BaseReply
{
    public Guid SagaId { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
