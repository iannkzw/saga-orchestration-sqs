namespace OrderService.Models;

public class Order
{
    public Guid Id { get; set; }
    public Guid? SagaId { get; set; }
    public decimal TotalAmount { get; set; }
    public string ItemsJson { get; set; } = string.Empty;
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
