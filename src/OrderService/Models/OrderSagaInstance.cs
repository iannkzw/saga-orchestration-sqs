namespace OrderService.Models;

public class OrderSagaInstance
{
    public Guid CorrelationId { get; set; }
    public Guid OrderId { get; set; }
    public string CurrentState { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
