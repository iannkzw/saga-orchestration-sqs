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

    public const string OrderStatusUpdates = "order-status-updates";

    // Nomes de todas as DLQs (sufixo "-dlq" para cada fila)
    public static readonly string[] AllDlqNames =
    [
        $"{OrderCommands}-dlq",
        $"{SagaCommands}-dlq",
        $"{PaymentCommands}-dlq",
        $"{PaymentReplies}-dlq",
        $"{InventoryCommands}-dlq",
        $"{InventoryReplies}-dlq",
        $"{ShippingCommands}-dlq",
        $"{ShippingReplies}-dlq",
        $"{OrderStatusUpdates}-dlq"
    ];

    // Mapeamento DLQ -> fila original
    public static readonly Dictionary<string, string> DlqToOriginalQueue = AllDlqNames
        .ToDictionary(dlq => dlq, dlq => dlq[..^4]); // remove "-dlq"
}
