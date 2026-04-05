# 01 — Controle de Status (State Machine)

← [Voltar ao índice](./00-overview.md) | Próximo: [02 — Idempotência →](./02-idempotency.md)

---

## Implementação Atual

A máquina de estados é implementada de forma imperativa e distribuída em múltiplos arquivos.

### `src/Shared/Contracts/` — Estados definidos como enum

```csharp
// src/SagaOrchestrator/Models/SagaState.cs
namespace SagaOrchestrator.Models;

public enum SagaState
{
    Pending,
    PaymentProcessing,
    InventoryReserving,
    ShippingScheduling,
    Completed,

    // Estados de compensação (ordem inversa)
    ShippingCancelling,
    InventoryReleasing,
    PaymentRefunding,
    Failed
}
```

### `src/SagaOrchestrator/StateMachine/SagaStateMachine.cs` — Lógica de transição

```csharp
// src/SagaOrchestrator/StateMachine/SagaStateMachine.cs
public record TransitionResult(SagaState NextState, string? CommandQueue);

public static class SagaStateMachine
{
    // Transições do happy path (em sucesso)
    private static readonly Dictionary<SagaState, TransitionResult> _forwardTransitions = new()
    {
        [SagaState.Pending]            = new(SagaState.PaymentProcessing,  "payment-commands"),
        [SagaState.PaymentProcessing]  = new(SagaState.InventoryReserving, "inventory-commands"),
        [SagaState.InventoryReserving] = new(SagaState.ShippingScheduling, "shipping-commands"),
        [SagaState.ShippingScheduling] = new(SagaState.Completed,          null),
    };

    // Transições de compensação (ao receber reply de compensação com sucesso)
    private static readonly Dictionary<SagaState, TransitionResult> _compensationTransitions = new()
    {
        [SagaState.ShippingCancelling] = new(SagaState.InventoryReleasing, "inventory-commands"),
        [SagaState.InventoryReleasing] = new(SagaState.PaymentRefunding,   "payment-commands"),
        [SagaState.PaymentRefunding]   = new(SagaState.Failed,              null),
    };

    // Ao falhar um step: qual é o primeiro estado de compensação?
    private static readonly Dictionary<SagaState, TransitionResult> _failureTransitions = new()
    {
        [SagaState.PaymentProcessing]  = new(SagaState.Failed,             null),
        [SagaState.InventoryReserving] = new(SagaState.PaymentRefunding,   "payment-commands"),
        [SagaState.ShippingScheduling] = new(SagaState.InventoryReleasing, "inventory-commands"),
    };

    public static TransitionResult? TryAdvance(SagaState currentState)
        => _forwardTransitions.GetValueOrDefault(currentState);

    public static TransitionResult? TryCompensate(SagaState failedState)
        => _failureTransitions.GetValueOrDefault(failedState);

    public static TransitionResult? TryAdvanceCompensation(SagaState currentCompensationState)
        => _compensationTransitions.GetValueOrDefault(currentCompensationState);

    public static bool IsTerminal(SagaState state)
        => state == SagaState.Completed || state == SagaState.Failed;

    public static bool IsCompensating(SagaState state)
        => state is SagaState.ShippingCancelling or SagaState.InventoryReleasing or SagaState.PaymentRefunding;
}
```

### `src/SagaOrchestrator/Models/SagaInstance.cs` — Método de transição

```csharp
// src/SagaOrchestrator/Models/SagaInstance.cs
public SagaStateTransition TransitionTo(SagaState newState, string triggeredBy)
{
    var transition = new SagaStateTransition
    {
        Id          = Guid.NewGuid(),
        SagaId      = Id,
        FromState   = CurrentState.ToString(),
        ToState     = newState.ToString(),
        TriggeredBy = triggeredBy,
        Timestamp   = DateTime.UtcNow
    };

    CurrentState = newState;
    UpdatedAt    = DateTime.UtcNow;
    Transitions.Add(transition);

    return transition;
}
```

### `src/SagaOrchestrator/Worker.cs` — Lógica de branching (execução imperativa)

```csharp
// src/SagaOrchestrator/Worker.cs — método ProcessReplyAsync
if (SagaStateMachine.IsCompensating(saga.CurrentState))
{
    await HandleCompensationReplyAsync(saga, success, mapping, db, ct);
}
else if (!success)
{
    await HandleFailureAsync(saga, baseReply, mapping, db, ct);
    activity?.SetStatus(ActivityStatusCode.Error, "Saga step failed — compensation triggered");
}
else
{
    await HandleSuccessAsync(saga, baseReply, mapping, db, ct);
}

// HandleSuccessAsync: avança estado e envia próximo comando
private async Task HandleSuccessAsync(...)
{
    StoreCompensationData(saga, replyJson);
    var result = SagaStateMachine.TryAdvance(saga.CurrentState);
    saga.TransitionTo(result.NextState, mapping.ReplyTypeName);
    if (result.CommandQueue is not null)
        await SendForwardCommandAsync(saga, result.CommandQueue, ct);
    await db.SaveChangesAsync(ct);
    if (SagaStateMachine.IsTerminal(saga.CurrentState))
        await PublishSagaTerminatedAsync(saga, ct);
}

// HandleFailureAsync: inicia compensação
private async Task HandleFailureAsync(...)
{
    var result = SagaStateMachine.TryCompensate(saga.CurrentState);
    saga.TransitionTo(result.NextState, $"{mapping.ReplyTypeName}:Failure");
    if (result.CommandQueue is not null)
        await SendCompensationCommandAsync(saga, result.CommandQueue, ct);
    await db.SaveChangesAsync(ct);
    if (SagaStateMachine.IsTerminal(saga.CurrentState))
        await PublishSagaTerminatedAsync(saga, ct);
}

// HandleCompensationReplyAsync: avança a cadeia de compensação
private async Task HandleCompensationReplyAsync(...)
{
    var result = SagaStateMachine.TryAdvanceCompensation(saga.CurrentState);
    saga.TransitionTo(result.NextState, $"{mapping.ReplyTypeName}:Compensated");
    if (result.CommandQueue is not null)
        await SendCompensationCommandAsync(saga, result.CommandQueue, ct);
    else
        await PublishSagaTerminatedAsync(saga, ct);
}
```

**Arquivos envolvidos:**
- `src/SagaOrchestrator/Models/SagaState.cs`
- `src/SagaOrchestrator/StateMachine/SagaStateMachine.cs`
- `src/SagaOrchestrator/Models/SagaInstance.cs`
- `src/SagaOrchestrator/Worker.cs` (métodos `HandleSuccessAsync`, `HandleFailureAsync`, `HandleCompensationReplyAsync`)

---

## Com MassTransit

A máquina de estados é declarativa. Estados, eventos e transições são definidos em uma única classe,
e o MassTransit gerencia a persistência, o roteamento e a execução automaticamente.

### `OrderSagaState.cs` — Estado da saga (substitui `SagaInstance` + `SagaState` enum)

```csharp
using MassTransit;

public class OrderSagaState : SagaStateMachineInstance
{
    // Obrigatório — correlaciona todas as mensagens desta saga
    public Guid CorrelationId { get; set; }

    // Estado atual (gerenciado pelo MassTransit)
    public string CurrentState { get; set; } = string.Empty;

    // Dados do pedido
    public Guid OrderId { get; set; }
    public decimal TotalAmount { get; set; }
    public string ItemsJson { get; set; } = "[]";
    public string? SimulateFailure { get; set; }

    // Dados de compensação — tipados, sem JSON manual
    public string? PaymentTransactionId { get; set; }
    public string? InventoryReservationId { get; set; }
    public string? ShippingTrackingNumber { get; set; }

    // Controle de concorrência otimista (substitui a dívida técnica atual)
    public byte[] RowVersion { get; set; } = [];

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

### `OrderStateMachine.cs` — Máquina de estados declarativa

```csharp
using MassTransit;

public class OrderStateMachine : MassTransitStateMachine<OrderSagaState>
{
    // ── Estados ──────────────────────────────────────────────────────────────
    public State PaymentProcessing  { get; private set; } = null!;
    public State InventoryReserving { get; private set; } = null!;
    public State ShippingScheduling { get; private set; } = null!;

    // Estados de compensação
    public State ShippingCancelling { get; private set; } = null!;
    public State InventoryReleasing { get; private set; } = null!;
    public State PaymentRefunding   { get; private set; } = null!;

    // ── Eventos ───────────────────────────────────────────────────────────────
    public Event<CreateOrder>           OrderCreated              { get; private set; } = null!;
    public Event<PaymentCompleted>      PaymentCompleted          { get; private set; } = null!;
    public Event<PaymentFailed>         PaymentFailed             { get; private set; } = null!;
    public Event<InventoryReserved>     InventoryReserved         { get; private set; } = null!;
    public Event<InventoryFailed>       InventoryFailed           { get; private set; } = null!;
    public Event<ShippingScheduled>     ShippingScheduled         { get; private set; } = null!;
    public Event<ShippingFailed>        ShippingFailed            { get; private set; } = null!;

    // Eventos de compensação
    public Event<ShippingCancelled>     ShippingCancelled         { get; private set; } = null!;
    public Event<InventoryReleased>     InventoryReleased         { get; private set; } = null!;
    public Event<PaymentRefunded>       PaymentRefunded           { get; private set; } = null!;

    public OrderStateMachine()
    {
        // Propriedade que armazena o estado atual no banco
        InstanceState(x => x.CurrentState);

        // ── Correlações ──────────────────────────────────────────────────────
        Event(() => OrderCreated,
            x => x.CorrelateById(ctx => ctx.Message.OrderId));
        Event(() => PaymentCompleted,
            x => x.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => PaymentFailed,
            x => x.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => InventoryReserved,
            x => x.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => InventoryFailed,
            x => x.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => ShippingScheduled,
            x => x.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => ShippingFailed,
            x => x.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => ShippingCancelled,
            x => x.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => InventoryReleased,
            x => x.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => PaymentRefunded,
            x => x.CorrelateById(ctx => ctx.Message.SagaId));

        // ── Happy Path ───────────────────────────────────────────────────────
        Initially(
            When(OrderCreated)
                .Then(ctx =>
                {
                    ctx.Saga.OrderId       = ctx.Message.OrderId;
                    ctx.Saga.TotalAmount   = ctx.Message.TotalAmount;
                    ctx.Saga.ItemsJson     = ctx.Message.ItemsJson;
                    ctx.Saga.SimulateFailure = ctx.Message.SimulateFailure;
                    ctx.Saga.CreatedAt     = DateTime.UtcNow;
                    ctx.Saga.UpdatedAt     = DateTime.UtcNow;
                })
                .Send(ctx => new ProcessPayment(ctx.Saga.CorrelationId, ctx.Saga.OrderId, ctx.Saga.TotalAmount))
                .TransitionTo(PaymentProcessing)
        );

        During(PaymentProcessing,
            When(PaymentCompleted)
                .Then(ctx =>
                {
                    ctx.Saga.PaymentTransactionId = ctx.Message.TransactionId;
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .Send(ctx => new ReserveInventory(ctx.Saga.CorrelationId, ctx.Saga.OrderId, ctx.Saga.ItemsJson))
                .TransitionTo(InventoryReserving),

            When(PaymentFailed)
                // Pagamento falhou — nada foi completado antes, vai direto para Failed
                .Then(ctx => ctx.Saga.UpdatedAt = DateTime.UtcNow)
                .Publish(ctx => new SagaTerminated(ctx.Saga.CorrelationId, ctx.Saga.OrderId, "Failed"))
                .Finalize()
        );

        During(InventoryReserving,
            When(InventoryReserved)
                .Then(ctx =>
                {
                    ctx.Saga.InventoryReservationId = ctx.Message.ReservationId;
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .Send(ctx => new ScheduleShipping(ctx.Saga.CorrelationId, ctx.Saga.OrderId, "Endereço padrão"))
                .TransitionTo(ShippingScheduling),

            When(InventoryFailed)
                // Estoque falhou — compensa pagamento
                .Then(ctx => ctx.Saga.UpdatedAt = DateTime.UtcNow)
                .Send(ctx => new RefundPayment(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.OrderId,
                    ctx.Saga.TotalAmount,
                    ctx.Saga.PaymentTransactionId!))
                .TransitionTo(PaymentRefunding)
        );

        During(ShippingScheduling,
            When(ShippingScheduled)
                .Then(ctx =>
                {
                    ctx.Saga.ShippingTrackingNumber = ctx.Message.TrackingNumber;
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .Publish(ctx => new SagaTerminated(ctx.Saga.CorrelationId, ctx.Saga.OrderId, "Completed"))
                .Finalize(),

            When(ShippingFailed)
                // Frete falhou — compensa estoque primeiro
                .Then(ctx => ctx.Saga.UpdatedAt = DateTime.UtcNow)
                .Send(ctx => new ReleaseInventory(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.OrderId,
                    ctx.Saga.InventoryReservationId!))
                .TransitionTo(InventoryReleasing)
        );

        // ── Cadeia de Compensação ────────────────────────────────────────────
        During(InventoryReleasing,
            When(InventoryReleased)
                .Then(ctx => ctx.Saga.UpdatedAt = DateTime.UtcNow)
                .Send(ctx => new RefundPayment(
                    ctx.Saga.CorrelationId,
                    ctx.Saga.OrderId,
                    ctx.Saga.TotalAmount,
                    ctx.Saga.PaymentTransactionId!))
                .TransitionTo(PaymentRefunding)
        );

        During(PaymentRefunding,
            When(PaymentRefunded)
                .Then(ctx => ctx.Saga.UpdatedAt = DateTime.UtcNow)
                .Publish(ctx => new SagaTerminated(ctx.Saga.CorrelationId, ctx.Saga.OrderId, "Failed"))
                .Finalize()
        );

        // Limpar instâncias finalizadas do repositório
        SetCompletedWhenFinalized();
    }
}
```

---

## Comparação Direta

| Aspecto | Atual | Com MassTransit |
|---|---|---|
| Definição de estados | `enum SagaState` (14 valores) | Propriedades `State` na classe da state machine |
| Lógica de transição | 3 dicionários estáticos + 3 métodos | Blocos `Initially`/`During`/`When` declarativos |
| Execução das transições | `HandleSuccessAsync`, `HandleFailureAsync`, `HandleCompensationReplyAsync` em `Worker.cs` | MassTransit executa automaticamente ao receber o evento correlacionado |
| Persistência do estado | Coluna `current_state` (string) em `sagas` via `SagaInstance.TransitionTo()` | Propriedade `CurrentState` em `OrderSagaState` persistida pelo saga repository do MassTransit |
| Happy path + compensação | Código imperativo com switches e ifs | Declarativo: `During(State, When(Event).Send(...).TransitionTo(...))` |

**Estimativa: ~250 linhas eliminadas.**

> Continua em [02 — Idempotência →](./02-idempotency.md)
