namespace SagaOrchestrator.Models;

public enum SagaState
{
    Pending,
    PaymentProcessing,
    InventoryReserving,
    ShippingScheduling,
    Completed
}
