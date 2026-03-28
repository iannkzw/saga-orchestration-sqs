namespace Shared.Configuration;

public static class SqsConfig
{
    public const string OrderCommands = "order-commands";
    public const string SagaCommands = "saga-commands";

    public const string PaymentCommands = "payment-commands";
    public const string PaymentReplies = "payment-replies";

    public const string InventoryCommands = "inventory-commands";
    public const string InventoryReplies = "inventory-replies";

    public const string ShippingCommands = "shipping-commands";
    public const string ShippingReplies = "shipping-replies";
}
