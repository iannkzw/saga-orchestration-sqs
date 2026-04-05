# 04 — Concorrência

← [03 — Reply / Correlação](./03-reply-correlation.md) | [Voltar ao índice](./00-overview.md) | Próximo: [05 — Compensação →](./05-compensation.md)

---

## Implementação Atual

Não há controle de concorrência. Isso é reconhecido explicitamente como dívida técnica em comentário
TODO no `Worker.cs`:

```csharp
// src/SagaOrchestrator/Worker.cs — ProcessReplyAsync
private async Task ProcessReplyAsync(Message message, QueueMapping mapping, string queueUrl, CancellationToken ct)
{
    // TODO [Dívida Técnica]: Validar se mapping.ReplyTypeName é esperado para saga.CurrentState.
    // Em re-entrega cruzada por timeout de visibilidade, um reply de estado anterior poderia
    // avançar a saga incorretamente. Aceitável para PoC; mitigar em produção.
    var baseReply = JsonSerializer.Deserialize<JsonElement>(message.Body);
    ...
}
```

### Cenário de problema — duas mensagens para a mesma saga

```
t=0: Mensagem A (PaymentReply success) chega → Worker lê saga, CurrentState=PaymentProcessing
t=1: Mensagem B (PaymentReply success, re-entrega) chega → Worker lê saga, CurrentState=PaymentProcessing
t=2: Worker A processa → saga.TransitionTo(InventoryReserving) → SaveChanges ✓
t=3: Worker B processa → saga.TransitionTo(InventoryReserving) → SaveChanges ✓ (DUPLICADO!)
```

Resultado: dois comandos `ReserveInventory` enviados para a mesma saga, potencialmente criando
duas reservas de estoque para o mesmo pedido.

### Por que não há proteção

```csharp
// src/SagaOrchestrator/Data/SagaDbContext.cs — sem RowVersion/concurrency token
modelBuilder.Entity<SagaInstance>(entity =>
{
    entity.ToTable("sagas");
    entity.HasKey(e => e.Id);
    entity.Property(e => e.CurrentState).HasColumnName("current_state")...;
    // RowVersion ausente — nenhum mecanismo de detecção de conflito
});
```

```csharp
// src/SagaOrchestrator/Models/SagaInstance.cs — sem propriedade de versão
public class SagaInstance
{
    public Guid Id { get; set; }
    public SagaState CurrentState { get; set; } = SagaState.Pending;
    // ... RowVersion ausente
}
```

**Arquivos com a dívida técnica:**
- `src/SagaOrchestrator/Worker.cs` (comentário TODO em `ProcessReplyAsync`)
- `src/SagaOrchestrator/Data/SagaDbContext.cs` (sem `IsRowVersion`)
- `src/SagaOrchestrator/Models/SagaInstance.cs` (sem `byte[] RowVersion`)

---

## Com MassTransit

O MassTransit resolve concorrência otimista nativamente com `RowVersion` + EF Core. Em caso de
conflito, ele faz retry automático com a versão mais recente da saga.

### `OrderSagaState.cs` — adicionar `RowVersion`

```csharp
public class OrderSagaState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = string.Empty;

    // ... outras propriedades ...

    // Controle de concorrência otimista — MassTransit gerencia automaticamente
    public byte[] RowVersion { get; set; } = [];
}
```

### `SagaDbContext.cs` — configurar `IsRowVersion()`

```csharp
// SagaDbContext.cs (versão MassTransit)
modelBuilder.Entity<OrderSagaState>(entity =>
{
    entity.HasKey(e => e.CorrelationId);

    entity.Property(e => e.CorrelationId).HasColumnName("correlation_id");
    entity.Property(e => e.CurrentState).HasColumnName("current_state").HasMaxLength(64);
    entity.Property(e => e.OrderId).HasColumnName("order_id");
    entity.Property(e => e.TotalAmount).HasColumnName("total_amount");

    // Configura concorrência otimista — gera colunas com xmin no PostgreSQL
    // ou rowversion no SQL Server
    entity.Property(e => e.RowVersion)
          .HasColumnName("row_version")
          .IsRowVersion();
});
```

### Registro do saga repository com `ConcurrencyMode.Optimistic`

```csharp
// Program.cs
cfg.AddSagaStateMachine<OrderStateMachine, OrderSagaState>()
   .EntityFrameworkRepository(r =>
   {
       // Ativa controle de concorrência otimista
       r.ConcurrencyMode = ConcurrencyMode.Optimistic;

       r.AddDbContext<DbContext, SagaDbContext>((provider, options) =>
           options.UseNpgsql(connectionString));
   });
```

### Como funciona o retry automático

```
t=0: Mensagem A (PaymentCompleted) chega → MassTransit lê saga (RowVersion=1)
t=1: Mensagem B (PaymentCompleted, re-entrega) chega → MassTransit lê saga (RowVersion=1)
t=2: Worker A processa → UPDATE sagas SET current_state='InventoryReserving', row_version=2 WHERE id=X AND row_version=1 ✓
t=3: Worker B processa → UPDATE sagas ... WHERE id=X AND row_version=1 → 0 rows affected
     └── DbUpdateConcurrencyException
     └── MassTransit faz retry automático → relê saga (RowVersion=2, CurrentState=InventoryReserving)
     └── Evento PaymentCompleted não tem handler para During(InventoryReserving) → descartado silenciosamente ✓
```

---

## Comparação Direta

| Aspecto | Atual | Com MassTransit |
|---|---|---|
| Controle de concorrência | **Ausente** — dívida técnica documentada em TODO | `RowVersion` + `ConcurrencyMode.Optimistic` |
| Detecção de conflito | Nenhuma — dois workers podem salvar o mesmo estado | `DbUpdateConcurrencyException` ao salvar |
| Recuperação de conflito | Não implementado | Retry automático pelo MassTransit |
| Configuração necessária | — | `IsRowVersion()` no EF + `ConcurrencyMode.Optimistic` no registro |
| Evento duplicado após conflito | Causa transição inválida | Descartado silenciosamente (estado não corresponde) |

**Estimativa: ~50 linhas adicionadas que resolvem a dívida técnica.**

> Continua em [05 — Compensação →](./05-compensation.md)
