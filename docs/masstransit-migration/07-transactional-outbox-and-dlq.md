# 07 — Transactional Outbox & DLQ

← [06 — Infraestrutura de Mensageria](./06-messaging-infrastructure.md) | [Voltar ao índice](./00-overview.md) | Próximo: [08 — Exemplo Completo →](./08-full-example.md)

---

## Implementação Atual

### Dívida Técnica: Dual-Write

O código atual envia mensagens SQS **antes** do `SaveChanges`, criando um problema de dual-write:
se o `SaveChanges` falhar após o `SendMessageAsync`, o comando já foi enviado mas o estado não foi
persistido, gerando inconsistência.

```csharp
// src/SagaOrchestrator/Worker.cs — HandleSuccessAsync
private async Task HandleSuccessAsync(
    SagaInstance saga,
    JsonElement replyJson,
    QueueMapping mapping,
    SagaDbContext db,
    CancellationToken ct)
{
    StoreCompensationData(saga, replyJson);

    var result = SagaStateMachine.TryAdvance(saga.CurrentState);
    saga.TransitionTo(result.NextState, mapping.ReplyTypeName);

    if (result.CommandQueue is not null)
    {
        // ⚠️ Enviar comando SQS ANTES do SaveChanges
        // Se SaveChanges falhar após esta linha, o comando já foi enviado
        // mas o estado da saga não foi atualizado — dual-write problem
        await SendForwardCommandAsync(saga, result.CommandQueue, ct);
    }

    // TODO [Transactional Outbox]: salvar comando na mesma tx do DB e publicar via job separado
    //      para garantir entrega exactly-once sem dual-write.
    await db.SaveChangesAsync(ct);   // ← pode falhar após SendMessageAsync
}
```

O mesmo problema existe em `HandleFailureAsync`:

```csharp
// src/SagaOrchestrator/Worker.cs — HandleFailureAsync
private async Task HandleFailureAsync(...)
{
    // ...
    if (result.CommandQueue is not null)
    {
        // ⚠️ Mesmo problema de dual-write
        await SendCompensationCommandAsync(saga, result.CommandQueue, ct);
    }

    // TODO [Transactional Outbox]: salvar comando na mesma tx do DB e publicar via job separado
    //      para garantir entrega exactly-once sem dual-write.
    await db.SaveChangesAsync(ct);
}
```

### DLQ — Endpoints manuais em `Program.cs`

O orquestrador expõe ~90 linhas de endpoints REST para visualização e reprocessamento manual de DLQs:

```csharp
// src/SagaOrchestrator/Program.cs — endpoint GET /dlq (~40 linhas)
app.MapGet("/dlq", async (IAmazonSQS sqs) =>
{
    var result = new Dictionary<string, object>();

    foreach (var dlqName in SqsConfig.AllDlqNames)
    {
        try
        {
            var queueUrl = (await sqs.GetQueueUrlAsync(dlqName)).QueueUrl;
            var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl              = queueUrl,
                MaxNumberOfMessages   = 10,
                VisibilityTimeout     = 0,  // peek — não esconde a mensagem
                MessageSystemAttributeNames = ["All"]
            });

            var messages = response.Messages.Select(m =>
            {
                object body;
                try { body = JsonSerializer.Deserialize<JsonElement>(m.Body); }
                catch { body = m.Body; }

                return new
                {
                    messageId              = m.MessageId,
                    receiptHandle          = m.ReceiptHandle,
                    body,
                    approximateReceiveCount = m.Attributes.GetValueOrDefault("ApproximateReceiveCount"),
                    sentTimestamp          = m.Attributes.GetValueOrDefault("SentTimestamp")
                };
            });

            result[dlqName] = new { count = messages.Count(), messages };
        }
        catch (Exception ex)
        {
            result[dlqName] = new { count = 0, messages = Array.Empty<object>(), error = ex.Message };
        }
    }

    return Results.Ok(result);
});

// src/SagaOrchestrator/Program.cs — endpoint POST /dlq/redrive (~50 linhas)
app.MapPost("/dlq/redrive", async (HttpContext context, IAmazonSQS sqs) =>
{
    var request = await context.Request.ReadFromJsonAsync<JsonElement>();

    var queueName     = request.GetProperty("queueName").GetString();
    var receiptHandle = request.GetProperty("receiptHandle").GetString();

    if (string.IsNullOrEmpty(queueName) || string.IsNullOrEmpty(receiptHandle))
        return Results.BadRequest("queueName e receiptHandle sao obrigatorios");

    if (!SqsConfig.DlqToOriginalQueue.TryGetValue(queueName, out var originalQueue))
        return Results.BadRequest($"DLQ desconhecida: {queueName}");

    var dlqUrl      = (await sqs.GetQueueUrlAsync(queueName)).QueueUrl;
    var originalUrl = (await sqs.GetQueueUrlAsync(originalQueue)).QueueUrl;

    var dlqMessages = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
    {
        QueueUrl            = dlqUrl,
        MaxNumberOfMessages = 1,
        VisibilityTimeout   = 30
    });

    var message = dlqMessages.Messages.FirstOrDefault(m => m.ReceiptHandle == receiptHandle);
    if (message is null)
        return Results.NotFound("Mensagem nao encontrada na DLQ. O receiptHandle pode ter expirado.");

    await sqs.SendMessageAsync(new SendMessageRequest
    {
        QueueUrl    = originalUrl,
        MessageBody = message.Body
    });

    await sqs.DeleteMessageAsync(dlqUrl, receiptHandle);

    return Results.Ok(new
    {
        redriven  = true,
        fromDlq   = queueName,
        toQueue   = originalQueue,
        messageId = message.MessageId
    });
});
```

**Arquivos envolvidos:**
- `src/SagaOrchestrator/Worker.cs` (TODO de dual-write em `HandleSuccessAsync` e `HandleFailureAsync`)
- `src/SagaOrchestrator/Program.cs` (endpoints `/dlq` e `/dlq/redrive`)
- `src/Shared/Configuration/SqsConfig.cs` (`AllDlqNames`, `DlqToOriginalQueue`)

---

## Com MassTransit

### Transactional Outbox — `AddEntityFrameworkOutbox`

O MassTransit salva as mensagens a publicar na **mesma transação do banco de dados** que salva o
estado da saga. Um job em background (Outbox Delivery Service) publica as mensagens depois que a
transação confirma. Isso elimina o dual-write completamente.

```csharp
// Program.cs
services.AddMassTransit(cfg =>
{
    cfg.AddSagaStateMachine<OrderStateMachine, OrderSagaState>()
       .EntityFrameworkRepository(r =>
       {
           r.ConcurrencyMode = ConcurrencyMode.Optimistic;
           r.AddDbContext<DbContext, SagaDbContext>((provider, options) =>
               options.UseNpgsql(connectionString));
       });

    // Outbox transacional — salva mensagens junto com o estado da saga
    cfg.AddEntityFrameworkOutbox<SagaDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();                                    // integra com o bus

        // Deduplicação: mensagens com mesmo MessageId em 1h são descartadas
        o.DuplicateDetectionWindow = TimeSpan.FromHours(1);

        // Intervalo de entrega do job de background
        o.QueryDelay = TimeSpan.FromSeconds(1);
    });

    cfg.UsingAmazonSqs((context, config) =>
    {
        config.Host("us-east-1", h => { ... });
        config.ConfigureEndpoints(context);
    });
});
```

### `SagaDbContext.cs` — tabelas do Outbox gerenciadas pelo MassTransit

```csharp
// SagaDbContext.cs
public class SagaDbContext : DbContext
{
    public SagaDbContext(DbContextOptions<SagaDbContext> options) : base(options) { }

    public DbSet<OrderSagaState> OrderSagas => Set<OrderSagaState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configuração da saga state
        modelBuilder.Entity<OrderSagaState>(entity =>
        {
            entity.HasKey(e => e.CorrelationId);
            entity.Property(e => e.RowVersion).IsRowVersion();
            // ...
        });

        // Adicionar tabelas do Outbox ao modelo EF
        // Cria: OutboxMessage, OutboxState, InboxState
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
```

### DLQ — gerenciamento automático pelo MassTransit

```csharp
// Program.cs — configuração de retry e error handling no endpoint
cfg.UsingAmazonSqs((context, config) =>
{
    config.Host("us-east-1", h => { ... });

    // Política de retry — antes de mover para a fila de erro
    config.UseMessageRetry(r =>
    {
        r.Intervals(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30));
    });

    // Política de circuit breaker
    config.UseCircuitBreaker(cb =>
    {
        cb.TrackingPeriod       = TimeSpan.FromMinutes(1);
        cb.TripThreshold        = 15;
        cb.ActiveThreshold      = 10;
        cb.ResetInterval        = TimeSpan.FromMinutes(5);
    });

    // Fila de erro (_error) e dead-letter (_skipped) criadas automaticamente
    // Sem código de DLQ manual necessário
    config.ConfigureEndpoints(context);
});
```

### Como o Outbox resolve o dual-write

```
Atual (dual-write problem):
    ① SendMessageAsync(ProcessPayment) → SQS ✓
    ② SaveChangesAsync()               → falha! ✗
    Resultado: mensagem enviada, estado não salvo → inconsistência

Com MassTransit Outbox:
    ① BeginTransaction()
    ② INSERT INTO outbox_messages (ProcessPayment payload)   → BD
    ③ UPDATE order_sagas SET current_state = 'PaymentProcessing'
    ④ CommitTransaction()  ← ÚNICO ponto de falha
    ⑤ [background job] SELECT * FROM outbox_messages WHERE NOT delivered
    ⑥ SendMessageAsync(ProcessPayment)  ← entrega garantida
    ⑦ UPDATE outbox_messages SET delivered = true
    Resultado: estado e mensagem sempre consistentes ✓
```

---

## Comparação Direta

| Aspecto | Atual | Com MassTransit |
|---|---|---|
| Dual-write | ⚠️ Presente — TODO documentado no código | Eliminado pelo Outbox transacional |
| Atomicidade | Não garantida entre SQS e PostgreSQL | Garantida — mensagem e estado na mesma transação |
| DLQ endpoint `/dlq` | ~40 linhas manuais em `Program.cs` | Substituído pela fila `_error` automática + dashboards |
| DLQ endpoint `/dlq/redrive` | ~50 linhas manuais em `Program.cs` | Console de gerenciamento do MassTransit |
| Retry de mensagens | Não implementado (DLQ manual) | Configurável: `UseMessageRetry` com intervalos e circuit breaker |
| Dead-letter | 9 DLQs manuais com nomenclatura `-dlq` | Filas `_error` e `_skipped` automáticas por endpoint |
| Deduplicação de entrega | Não garantida (dual-write pode causar re-envio) | `DuplicateDetectionWindow` no Outbox |

**Estimativa: resolve dívida técnica de dual-write + ~90 linhas de endpoints de DLQ eliminadas.**

> Continua em [08 — Exemplo Completo →](./08-full-example.md)
