# 05 — Compensação

← [04 — Concorrência](./04-concurrency.md) | [Voltar ao índice](./00-overview.md) | Próximo: [06 — Infraestrutura de Mensageria →](./06-messaging-infrastructure.md)

---

## Implementação Atual

Os dados necessários para compensação (IDs de transação, reserva, rastreamento) são armazenados como
um dicionário serializado em JSON em uma única coluna do banco. A lógica de compensação é imperativa
e distribuída entre vários métodos em `Worker.cs`.

### `src/SagaOrchestrator/Models/SagaInstance.cs` — `CompensationDataJson`

```csharp
// src/SagaOrchestrator/Models/SagaInstance.cs
public class SagaInstance
{
    public Guid Id { get; set; }
    public SagaState CurrentState { get; set; } = SagaState.Pending;
    public decimal TotalAmount { get; set; }
    public string ItemsJson { get; set; } = "[]";

    // Dados de compensação: dicionário serializado como string JSON
    // Ex: {"TransactionId":"abc","ReservationId":"xyz","TrackingNumber":"TRK-001"}
    public string CompensationDataJson { get; set; } = "{}";

    // ...
}
```

### `src/SagaOrchestrator/Worker.cs` — `StoreCompensationData`

Método chamado a cada step do happy path para guardar os IDs necessários para desfazer:

```csharp
// src/SagaOrchestrator/Worker.cs
private void StoreCompensationData(SagaInstance saga, JsonElement replyJson)
{
    // Desserializar dicionário atual
    var data = JsonSerializer.Deserialize<Dictionary<string, string>>(saga.CompensationDataJson)
        ?? new Dictionary<string, string>();

    // Adicionar dados específicos do step que completou
    switch (saga.CurrentState)
    {
        case SagaState.PaymentProcessing when replyJson.TryGetProperty("TransactionId", out var tid):
            data["TransactionId"] = tid.GetString() ?? string.Empty;
            break;
        case SagaState.InventoryReserving when replyJson.TryGetProperty("ReservationId", out var rid):
            data["ReservationId"] = rid.GetString() ?? string.Empty;
            break;
        case SagaState.ShippingScheduling when replyJson.TryGetProperty("TrackingNumber", out var tn):
            data["TrackingNumber"] = tn.GetString() ?? string.Empty;
            break;
    }

    // Re-serializar e salvar de volta na coluna
    saga.CompensationDataJson = JsonSerializer.Serialize(data);
}
```

### `src/SagaOrchestrator/Worker.cs` — `GetCompensationData`

```csharp
// src/SagaOrchestrator/Worker.cs
private Dictionary<string, string> GetCompensationData(SagaInstance saga)
{
    return JsonSerializer.Deserialize<Dictionary<string, string>>(saga.CompensationDataJson)
        ?? new Dictionary<string, string>();
}
```

### `src/SagaOrchestrator/Worker.cs` — `SendCompensationCommandAsync`

Constrói o comando de compensação adequado baseado no estado atual, recuperando os IDs do dicionário:

```csharp
// src/SagaOrchestrator/Worker.cs
private async Task SendCompensationCommandAsync(SagaInstance saga, string commandQueue, CancellationToken ct)
{
    var compData = GetCompensationData(saga);  // Desserializa dicionário JSON

    object command = saga.CurrentState switch
    {
        SagaState.PaymentRefunding => new RefundPayment
        {
            SagaId         = saga.Id,
            OrderId        = saga.OrderId,
            Amount         = saga.TotalAmount,
            TransactionId  = compData.GetValueOrDefault("TransactionId", string.Empty),
            IdempotencyKey = $"{saga.Id}-refund-payment",
            Timestamp      = DateTime.UtcNow
        },
        SagaState.InventoryReleasing => new ReleaseInventory
        {
            SagaId         = saga.Id,
            OrderId        = saga.OrderId,
            ReservationId  = compData.GetValueOrDefault("ReservationId", string.Empty),
            IdempotencyKey = $"{saga.Id}-release-inventory",
            Timestamp      = DateTime.UtcNow
        },
        SagaState.ShippingCancelling => new CancelShipping
        {
            SagaId          = saga.Id,
            OrderId         = saga.OrderId,
            TrackingNumber  = compData.GetValueOrDefault("TrackingNumber", string.Empty),
            IdempotencyKey  = $"{saga.Id}-cancel-shipping",
            Timestamp       = DateTime.UtcNow
        },
        _ => throw new InvalidOperationException($"Comando de compensacao nao mapeado para estado {saga.CurrentState}")
    };

    await SendCommandToQueueAsync(command, commandQueue, null, ct);
}
```

### `src/SagaOrchestrator/Worker.cs` — `HandleCompensationReplyAsync`

Avança a cadeia de compensação ao receber a confirmação de cada step compensatório:

```csharp
// src/SagaOrchestrator/Worker.cs
private async Task HandleCompensationReplyAsync(
    SagaInstance saga,
    bool success,
    QueueMapping mapping,
    SagaDbContext db,
    CancellationToken ct)
{
    if (!success)
    {
        // Compensação falhou — intervenção manual necessária
        _logger.LogError(
            "Falha na compensacao da saga {SagaId} no estado {State}. Intervencao manual necessaria.",
            saga.Id, saga.CurrentState);
        db.SagaStateTransitions.Add(saga.TransitionTo(SagaState.Failed, $"{mapping.ReplyTypeName}:CompensationFailure"));
        await db.SaveChangesAsync(ct);
        await PublishSagaTerminatedAsync(saga, ct);
        return;
    }

    // Avançar para próximo estado de compensação
    var result = SagaStateMachine.TryAdvanceCompensation(saga.CurrentState);
    if (result is null) return;

    db.SagaStateTransitions.Add(saga.TransitionTo(result.NextState, $"{mapping.ReplyTypeName}:Compensated"));
    await db.SaveChangesAsync(ct);

    if (result.CommandQueue is not null)
        await SendCompensationCommandAsync(saga, result.CommandQueue, ct);
    else
        await PublishSagaTerminatedAsync(saga, ct);
}
```

**Arquivos envolvidos:**
- `src/SagaOrchestrator/Models/SagaInstance.cs` (campo `CompensationDataJson`)
- `src/SagaOrchestrator/Worker.cs` (`StoreCompensationData`, `GetCompensationData`, `SendCompensationCommandAsync`, `HandleCompensationReplyAsync`)
- `src/SagaOrchestrator/StateMachine/SagaStateMachine.cs` (dicionários `_compensationTransitions`, `_failureTransitions`)

---

## Com MassTransit

Os dados de compensação são propriedades tipadas diretamente na saga state. A cadeia de compensação
é declarada como blocos `During`/`When` na state machine, sem código imperativo separado.

### `OrderSagaState.cs` — propriedades tipadas para compensação

```csharp
// OrderSagaState.cs — dados de compensação como propriedades de primeira classe
public class OrderSagaState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = string.Empty;

    public Guid OrderId { get; set; }
    public decimal TotalAmount { get; set; }
    public string ItemsJson { get; set; } = "[]";

    // Dados de compensação — tipados, sem JSON manual
    public string? PaymentTransactionId { get; set; }     // substitui data["TransactionId"]
    public string? InventoryReservationId { get; set; }   // substitui data["ReservationId"]
    public string? ShippingTrackingNumber { get; set; }   // substitui data["TrackingNumber"]

    public byte[] RowVersion { get; set; } = [];
}
```

### `OrderStateMachine.cs` — compensação como blocos declarativos

```csharp
// OrderStateMachine.cs — happy path armazena dados de compensação inline
During(PaymentProcessing,
    When(PaymentCompleted)
        .Then(ctx =>
        {
            // Armazenar TransactionId diretamente como propriedade tipada
            ctx.Saga.PaymentTransactionId = ctx.Message.TransactionId;
            ctx.Saga.UpdatedAt = DateTime.UtcNow;
        })
        .Send(ctx => new ReserveInventory(ctx.Saga.CorrelationId, ctx.Saga.OrderId, ctx.Saga.ItemsJson))
        .TransitionTo(InventoryReserving),

    When(PaymentFailed)
        // Nada a compensar — vai direto para Failed
        .Finalize()
);

During(InventoryReserving,
    When(InventoryReserved)
        .Then(ctx =>
        {
            // Armazenar ReservationId diretamente
            ctx.Saga.InventoryReservationId = ctx.Message.ReservationId;
            ctx.Saga.UpdatedAt = DateTime.UtcNow;
        })
        .Send(ctx => new ScheduleShipping(ctx.Saga.CorrelationId, ctx.Saga.OrderId, "Endereço padrão"))
        .TransitionTo(ShippingScheduling),

    When(InventoryFailed)
        // Compensar pagamento — usa TransactionId da propriedade tipada
        .Send(ctx => new RefundPayment(
            ctx.Saga.CorrelationId,
            ctx.Saga.OrderId,
            ctx.Saga.TotalAmount,
            ctx.Saga.PaymentTransactionId!))   // acesso tipado, sem GetValueOrDefault
        .TransitionTo(PaymentRefunding)
);

During(ShippingScheduling,
    When(ShippingScheduled)
        .Then(ctx =>
        {
            // Armazenar TrackingNumber diretamente
            ctx.Saga.ShippingTrackingNumber = ctx.Message.TrackingNumber;
            ctx.Saga.UpdatedAt = DateTime.UtcNow;
        })
        .Publish(ctx => new SagaTerminated(ctx.Saga.CorrelationId, ctx.Saga.OrderId, "Completed"))
        .Finalize(),

    When(ShippingFailed)
        // Compensar estoque primeiro — usa ReservationId da propriedade tipada
        .Send(ctx => new ReleaseInventory(
            ctx.Saga.CorrelationId,
            ctx.Saga.OrderId,
            ctx.Saga.InventoryReservationId!))
        .TransitionTo(InventoryReleasing)
);

// ── Cadeia de Compensação ────────────────────────────────────────────────────

During(InventoryReleasing,
    When(InventoryReleased)
        // Compensar pagamento — usa TransactionId tipado
        .Send(ctx => new RefundPayment(
            ctx.Saga.CorrelationId,
            ctx.Saga.OrderId,
            ctx.Saga.TotalAmount,
            ctx.Saga.PaymentTransactionId!))
        .TransitionTo(PaymentRefunding)
);

During(PaymentRefunding,
    When(PaymentRefunded)
        // Cadeia de compensação completa
        .Publish(ctx => new SagaTerminated(ctx.Saga.CorrelationId, ctx.Saga.OrderId, "Failed"))
        .Finalize()
);
```

---

## Comparação Direta

| Aspecto | Atual | Com MassTransit |
|---|---|---|
| Armazenamento dos dados | `CompensationDataJson` — dicionário serializado como string JSON em coluna única | Propriedades tipadas (`PaymentTransactionId`, `InventoryReservationId`, `ShippingTrackingNumber`) |
| Acesso aos dados | `compData.GetValueOrDefault("TransactionId", string.Empty)` | `ctx.Saga.PaymentTransactionId!` |
| Lógica de compensação | Métodos separados: `StoreCompensationData`, `GetCompensationData`, `SendCompensationCommandAsync`, `HandleCompensationReplyAsync` | Blocos `During(State, When(Event).Send(...))` na state machine |
| Cadeia de compensação | Dicionário `_compensationTransitions` + `TryAdvanceCompensation()` | Declarativa: cada estado de compensação tem seu `During` |
| Falha na compensação | Tratada em `HandleCompensationReplyAsync` com log de erro | Tratada em `During(State, When(CompensationFailed).Finalize())` |
| Type safety | Fraca — string key em dicionário, acesso por `GetValueOrDefault` | Forte — propriedades tipadas com verificação em compile-time |

**Estimativa: ~130 linhas eliminadas.**

> Continua em [06 — Infraestrutura de Mensageria →](./06-messaging-infrastructure.md)
