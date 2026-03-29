using SagaOrchestrator.Models;

namespace SagaOrchestrator.StateMachine;

public record TransitionResult(SagaState NextState, string? CommandQueue);

public static class SagaStateMachine
{
    // Happy path transitions (on success)
    private static readonly Dictionary<SagaState, TransitionResult> _forwardTransitions = new()
    {
        [SagaState.Pending] = new(SagaState.PaymentProcessing, "payment-commands"),
        [SagaState.PaymentProcessing] = new(SagaState.InventoryReserving, "inventory-commands"),
        [SagaState.InventoryReserving] = new(SagaState.ShippingScheduling, "shipping-commands"),
        [SagaState.ShippingScheduling] = new(SagaState.Completed, null),
    };

    // Compensation transitions (on compensation reply success)
    private static readonly Dictionary<SagaState, TransitionResult> _compensationTransitions = new()
    {
        [SagaState.ShippingCancelling] = new(SagaState.InventoryReleasing, "inventory-commands"),
        [SagaState.InventoryReleasing] = new(SagaState.PaymentRefunding, "payment-commands"),
        [SagaState.PaymentRefunding] = new(SagaState.Failed, null),
    };

    // When a step fails, determine the first compensation state based on what was already completed.
    // The failing step itself does NOT need compensation (it failed, so nothing to undo).
    // We compensate the steps that succeeded BEFORE the failing step, in reverse order.
    private static readonly Dictionary<SagaState, TransitionResult> _failureTransitions = new()
    {
        // Payment failed → nothing completed before, go straight to Failed
        [SagaState.PaymentProcessing] = new(SagaState.Failed, null),
        // Inventory failed → compensate Payment
        [SagaState.InventoryReserving] = new(SagaState.PaymentRefunding, "payment-commands"),
        // Shipping failed → compensate Inventory first, then Payment
        [SagaState.ShippingScheduling] = new(SagaState.InventoryReleasing, "inventory-commands"),
    };

    public static TransitionResult? TryAdvance(SagaState currentState)
    {
        return _forwardTransitions.GetValueOrDefault(currentState);
    }

    public static TransitionResult? TryCompensate(SagaState failedState)
    {
        return _failureTransitions.GetValueOrDefault(failedState);
    }

    public static TransitionResult? TryAdvanceCompensation(SagaState currentCompensationState)
    {
        return _compensationTransitions.GetValueOrDefault(currentCompensationState);
    }

    public static bool IsTerminal(SagaState state) =>
        state == SagaState.Completed || state == SagaState.Failed;

    public static bool IsCompensating(SagaState state) =>
        state is SagaState.ShippingCancelling or SagaState.InventoryReleasing or SagaState.PaymentRefunding;
}
