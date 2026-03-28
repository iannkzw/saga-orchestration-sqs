using SagaOrchestrator.Models;

namespace SagaOrchestrator.StateMachine;

public record TransitionResult(SagaState NextState, string? CommandQueue);

public static class SagaStateMachine
{
    private static readonly Dictionary<SagaState, TransitionResult> _transitions = new()
    {
        [SagaState.Pending] = new(SagaState.PaymentProcessing, "payment-commands"),
        [SagaState.PaymentProcessing] = new(SagaState.InventoryReserving, "inventory-commands"),
        [SagaState.InventoryReserving] = new(SagaState.ShippingScheduling, "shipping-commands"),
        [SagaState.ShippingScheduling] = new(SagaState.Completed, null),
    };

    public static TransitionResult? TryAdvance(SagaState currentState)
    {
        return _transitions.GetValueOrDefault(currentState);
    }

    public static bool IsTerminal(SagaState state) => state == SagaState.Completed;
}
