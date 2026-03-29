namespace SagaOrchestrator.Models;

public enum SagaState
{
    Pending,
    PaymentProcessing,
    InventoryReserving,
    ShippingScheduling,
    Completed,

    // Compensation states (reverse order)
    ShippingCancelling,
    InventoryReleasing,
    PaymentRefunding,
    Failed
}
