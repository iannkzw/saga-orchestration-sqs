using Microsoft.EntityFrameworkCore;
using OrderService.Models;

namespace OrderService.Data;

public class OrderDbContext : DbContext
{
    public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options) { }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderSagaInstance> SagaInstances => Set<OrderSagaInstance>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("orders");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.SagaId).HasColumnName("saga_id");
            entity.Property(e => e.TotalAmount).HasColumnName("total_amount");
            entity.Property(e => e.ItemsJson).HasColumnName("items_json");
            entity.Property(e => e.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<OrderSagaInstance>(entity =>
        {
            entity.ToTable("order_saga_instances");
            entity.HasKey(e => e.CorrelationId);
            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id");
            entity.Property(e => e.OrderId).HasColumnName("order_id");
            entity.Property(e => e.CurrentState).HasColumnName("current_state").HasMaxLength(64);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        });
    }
}
