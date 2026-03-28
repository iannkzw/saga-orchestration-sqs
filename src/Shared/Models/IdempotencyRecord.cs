namespace Shared.Models;

public class IdempotencyRecord
{
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTime ProcessedAt { get; set; }
    public string? ResponsePayload { get; set; }
}
