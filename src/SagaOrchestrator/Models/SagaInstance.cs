namespace SagaOrchestrator.Models;

public class SagaInstance
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public SagaState CurrentState { get; set; } = SagaState.Pending;
    public decimal TotalAmount { get; set; }
    public string ItemsJson { get; set; } = "[]";
    public string CompensationDataJson { get; set; } = "{}";
    public string? SimulateFailure { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<SagaStateTransition> Transitions { get; set; } = [];

    public SagaStateTransition TransitionTo(SagaState newState, string triggeredBy)
    {
        var transition = new SagaStateTransition
        {
            Id = Guid.NewGuid(),
            SagaId = Id,
            FromState = CurrentState.ToString(),
            ToState = newState.ToString(),
            TriggeredBy = triggeredBy,
            Timestamp = DateTime.UtcNow
        };

        CurrentState = newState;
        UpdatedAt = DateTime.UtcNow;
        Transitions.Add(transition);

        return transition;
    }
}
