using MassTransit;

namespace OrderService.Models;

public class OrderSagaInstance : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = string.Empty;
    public Guid OrderId { get; set; }
    public string CustomerId { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string ItemsJson { get; set; } = "[]";
    public string? PaymentId { get; set; }
    public string? ReservationId { get; set; }
    public string? CompensationStep { get; set; }
    public string? FailureReason { get; set; }
    public string? SimulateFailure { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public uint xmin { get; set; }
}
