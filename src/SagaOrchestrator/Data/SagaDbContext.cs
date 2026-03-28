using Microsoft.EntityFrameworkCore;
using SagaOrchestrator.Models;

namespace SagaOrchestrator.Data;

public class SagaDbContext : DbContext
{
    public SagaDbContext(DbContextOptions<SagaDbContext> options) : base(options) { }

    public DbSet<SagaInstance> Sagas => Set<SagaInstance>();
    public DbSet<SagaStateTransition> SagaStateTransitions => Set<SagaStateTransition>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SagaInstance>(entity =>
        {
            entity.ToTable("sagas");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OrderId).HasColumnName("order_id");
            entity.Property(e => e.CurrentState)
                .HasColumnName("current_state")
                .HasConversion<string>()
                .HasMaxLength(50);
            entity.Property(e => e.TotalAmount).HasColumnName("total_amount");
            entity.Property(e => e.ItemsJson).HasColumnName("items_json");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");

            entity.HasMany(e => e.Transitions)
                .WithOne(t => t.Saga)
                .HasForeignKey(t => t.SagaId);
        });

        modelBuilder.Entity<SagaStateTransition>(entity =>
        {
            entity.ToTable("saga_state_transitions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.SagaId).HasColumnName("saga_id");
            entity.Property(e => e.FromState).HasColumnName("from_state").HasMaxLength(50);
            entity.Property(e => e.ToState).HasColumnName("to_state").HasMaxLength(50);
            entity.Property(e => e.TriggeredBy).HasColumnName("triggered_by").HasMaxLength(100);
            entity.Property(e => e.Timestamp).HasColumnName("timestamp");
        });
    }
}
