namespace SagaOrchestrator.Models;

public class SagaStateTransition
{
    public Guid Id { get; set; }
    public Guid SagaId { get; set; }
    public string FromState { get; set; } = string.Empty;
    public string ToState { get; set; } = string.Empty;
    public string TriggeredBy { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public SagaInstance Saga { get; set; } = null!;
}
