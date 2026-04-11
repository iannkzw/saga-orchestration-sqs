# Design: mt-state-machine

## Estrutura da OrderStateMachine

```csharp
public class OrderStateMachine : MassTransitStateMachine<OrderSagaInstance>
{
    // Estados declarados
    public State PaymentProcessing { get; private set; }
    public State InventoryReserving { get; private set; }
    public State ShippingScheduling { get; private set; }
    public State Compensating { get; private set; }

    // Eventos declarados
    public Event<OrderPlaced> OrderPlaced { get; private set; }
    public Event<PaymentCompleted> PaymentCompleted { get; private set; }
    public Event<PaymentFailed> PaymentFailed { get; private set; }
    public Event<InventoryReserved> InventoryReserved { get; private set; }
    public Event<InventoryFailed> InventoryFailed { get; private set; }
    public Event<ShippingScheduled> ShippingScheduled { get; private set; }
    public Event<ShippingFailed> ShippingFailed { get; private set; }
    public Event<PaymentCancelled> PaymentCancelled { get; private set; }
    public Event<InventoryCancelled> InventoryCancelled { get; private set; }
}
```

## Fluxo Happy Path

```
OrderPlaced
  → TransitionTo(PaymentProcessing)
  → Then: salva PaymentId na instância
  → PublishAsync: ProcessPayment { OrderId, Amount, SagaId }

PaymentCompleted (during PaymentProcessing)
  → TransitionTo(InventoryReserving)
  → Then: instance.PaymentId = context.Data.PaymentId
  → PublishAsync: ReserveInventory { OrderId, Items, SagaId }

InventoryReserved (during InventoryReserving)
  → TransitionTo(ShippingScheduling)
  → Then: instance.ReservationId = context.Data.ReservationId
  → PublishAsync: ScheduleShipping { OrderId, Address, SagaId }

ShippingScheduled (during ShippingScheduling)
  → TransitionTo(Final)
  → Then: Order.Status = Completed
```

## Fluxo de Compensação

```
PaymentFailed (during PaymentProcessing)
  → TransitionTo(Final)
  → Then: Order.Status = Failed, instance.FailureReason = context.Data.Reason

InventoryFailed (during InventoryReserving)
  → TransitionTo(Compensating)
  → Then: instance.CompensationStep = "CancelPayment"
  → PublishAsync: CancelPayment { PaymentId, SagaId }

ShippingFailed (during ShippingScheduling)
  → TransitionTo(Compensating)
  → Then: instance.CompensationStep = "CancelInventory"
  → PublishAsync: CancelInventory { ReservationId, SagaId }

InventoryCancelled (during Compensating, when CompensationStep = "CancelInventory")
  → Then: instance.CompensationStep = "CancelPayment"
  → PublishAsync: CancelPayment { PaymentId, SagaId }

PaymentCancelled (during Compensating)
  → TransitionTo(Final)
  → Then: Order.Status = Failed
```

## Decisões de Design

### D1 — CorrelationId = OrderId

**Motivo:** Um pedido mapeia 1:1 com uma saga. Usar OrderId como CorrelationId elimina a necessidade de um SagaId separado, simplifica a rastreabilidade e permite que `GET /orders/{id}` retorne o estado da saga diretamente.

**Impacto nos eventos:** Todos os eventos devem conter `Guid OrderId` (= CorrelationId). Os consumidores dos serviços publicam eventos com o mesmo `OrderId` recebido.

### D2 — Order.Status atualizado no Then()

**Motivo:** Com a saga rodando dentro do `OrderService`, temos acesso direto ao `OrderDbContext` no `Then()` block via `IServiceProvider` ou injeção no construtor da state machine.

**Implementação:**
```csharp
.Then(context => {
    // Acesso ao DbContext via IStateMachineActivityFactory ou via Then com serviço
    context.Saga.UpdatedAt = DateTime.UtcNow;
    // Order.Status será atualizado via IOrderRepository injetado no Then activity
})
```

**Alternativa escolhida:** Usar `Activity<OrderSagaInstance>` separada para atualizar `Order.Status`. Mantém a state machine limpa e testável.

### D3 — CompensationStep como string na instância

**Motivo:** Evita sub-estados de compensação (ex: `CompensatingInventory`, `CompensatingPayment`) que duplicariam a lógica. Um único estado `Compensating` com a propriedade `CompensationStep` é mais simples.

**Valores:** `"CancelInventory"` → `"CancelPayment"` → null (Final)

### D4 — Sem timeout nesta feature

**Motivo:** Timeout/retry é complexidade adicional fora do escopo do PoC. Os eventos de falha explícitos (`PaymentFailed`, etc.) cobrem os casos de erro. Timeouts podem ser adicionados futuramente com `Schedule<>`.

## Mapeamento EF Core

```csharp
// OrderDbContext.cs
modelBuilder.Entity<OrderSagaInstance>(e => {
    e.ToTable("order_saga_instances");
    e.HasKey(x => x.CorrelationId);
    e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
    e.Property(x => x.CurrentState).HasColumnName("current_state");
    e.Property(x => x.OrderId).HasColumnName("order_id");
    e.Property(x => x.PaymentId).HasColumnName("payment_id");
    e.Property(x => x.ReservationId).HasColumnName("reservation_id");
    e.Property(x => x.CompensationStep).HasColumnName("compensation_step");
    e.Property(x => x.FailureReason).HasColumnName("failure_reason");
    e.Property(x => x.CreatedAt).HasColumnName("created_at");
    e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
});
```
