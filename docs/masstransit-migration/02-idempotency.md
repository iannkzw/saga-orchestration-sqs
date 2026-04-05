# 02 — Idempotência

← [01 — State Machine](./01-state-machine.md) | [Voltar ao índice](./00-overview.md) | Próximo: [03 — Reply / Correlação →](./03-reply-correlation.md)

---

## Implementação Atual

A idempotência é implementada manualmente com uma tabela PostgreSQL dedicada e verificação explícita
em cada serviço consumidor.

### `src/Shared/Idempotency/IdempotencyStore.cs` — Store manual com PostgreSQL

```csharp
// src/Shared/Idempotency/IdempotencyStore.cs
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Shared.Idempotency;

public class IdempotencyStore
{
    private readonly string? _connectionString;
    private readonly ILogger<IdempotencyStore> _logger;

    public IdempotencyStore(IConfiguration configuration, ILogger<IdempotencyStore> logger)
    {
        _connectionString = configuration.GetConnectionString("SagaDb");
        _logger = logger;
    }

    // DDL — cria a tabela se não existir
    public async Task EnsureTableAsync()
    {
        if (_connectionString is null) return;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS idempotency_keys (
                idempotency_key VARCHAR(256) PRIMARY KEY,
                saga_id         UUID NOT NULL,
                result_json     TEXT NOT NULL,
                created_at      TIMESTAMP NOT NULL DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS idx_idempotency_saga_id ON idempotency_keys (saga_id);
            """;
        await cmd.ExecuteNonQueryAsync();

        _logger.LogInformation("Tabela idempotency_keys garantida no PostgreSQL");
    }

    // Verificação — retorna resultado anterior se chave já foi processada
    public async Task<T?> TryGetAsync<T>(string idempotencyKey) where T : class
    {
        if (_connectionString is null) return null;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT result_json FROM idempotency_keys WHERE idempotency_key = @key";
        cmd.Parameters.AddWithValue("key", idempotencyKey);

        var result = await cmd.ExecuteScalarAsync();
        if (result is null or DBNull) return null;

        _logger.LogInformation(
            "Idempotency hit: chave {Key} ja processada, retornando resultado anterior",
            idempotencyKey);
        return JsonSerializer.Deserialize<T>((string)result);
    }

    // Gravação — salva resultado com INSERT ... ON CONFLICT DO NOTHING
    public async Task SaveAsync<T>(string idempotencyKey, Guid sagaId, T result)
    {
        if (_connectionString is null) return;

        var json = JsonSerializer.Serialize(result);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO idempotency_keys (idempotency_key, saga_id, result_json)
            VALUES (@key, @sagaId, @json)
            ON CONFLICT (idempotency_key) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("key", idempotencyKey);
        cmd.Parameters.AddWithValue("sagaId", sagaId);
        cmd.Parameters.AddWithValue("json", json);

        await cmd.ExecuteNonQueryAsync();
    }
}
```

### Verificação em cada consumer — exemplo `PaymentService/Worker.cs`

O mesmo padrão se repete em **todos os 3 serviços** (Payment, Inventory, Shipping), nos handlers de
forward e de compensação:

```csharp
// src/PaymentService/Worker.cs — HandleProcessPaymentAsync
private async Task HandleProcessPaymentAsync(Message message, string replyQueueUrl, CancellationToken ct)
{
    var command = JsonSerializer.Deserialize<ProcessPayment>(message.Body)!;

    // ① Verificar idempotência — busca resultado anterior no PostgreSQL
    var cachedReply = await _idempotencyStore.TryGetAsync<PaymentReply>(command.IdempotencyKey);
    if (cachedReply is not null)
    {
        // Reenviar o reply cached diretamente para a fila de replies
        var replyRequest = new SendMessageRequest
        {
            QueueUrl         = replyQueueUrl,
            MessageBody      = JsonSerializer.Serialize(cachedReply),
            MessageAttributes = new Dictionary<string, MessageAttributeValue>()
        };
        SqsTracePropagation.Inject(replyRequest.MessageAttributes);
        await _sqs.SendMessageAsync(replyRequest, ct);
        return;
    }

    // ② Processar normalmente...
    var reply = new PaymentReply { SagaId = command.SagaId, Success = true, TransactionId = Guid.NewGuid().ToString() };

    // ③ Salvar resultado para futuras re-tentativas
    await _idempotencyStore.SaveAsync(command.IdempotencyKey, command.SagaId, reply);

    // ④ Enviar reply real
    await _sqs.SendMessageAsync(...);
}
```

O mesmo padrão existe em:
- `HandleRefundPaymentAsync` (PaymentService)
- `HandleReserveInventoryAsync` / `HandleReleaseInventoryAsync` (InventoryService)
- `HandleScheduleShippingAsync` / `HandleCancelShippingAsync` (ShippingService)

### `IdempotencyKey` gerado pelo orquestrador (`src/SagaOrchestrator/Worker.cs`)

```csharp
// src/SagaOrchestrator/Worker.cs — SendForwardCommandAsync e SendCompensationCommandAsync
SagaState.InventoryReserving => new ReserveInventory
{
    SagaId         = saga.Id,
    IdempotencyKey = $"{saga.Id}-inventory",   // chave determinística por saga + step
    ...
},

SagaState.PaymentRefunding => new RefundPayment
{
    SagaId         = saga.Id,
    IdempotencyKey = $"{saga.Id}-refund-payment",
    ...
},
```

**Arquivos envolvidos:**
- `src/Shared/Idempotency/IdempotencyStore.cs`
- `src/Shared/Contracts/Commands/BaseCommand.cs` (campo `IdempotencyKey`)
- `src/PaymentService/Worker.cs`
- `src/InventoryService/Worker.cs`
- `src/ShippingService/Worker.cs`
- `src/SagaOrchestrator/Worker.cs` (geração das chaves)

---

## Com MassTransit

O MassTransit resolve idempotência em duas camadas complementares:

### Camada 1 — Correlação do saga repository

Quando um evento duplicado chega (por re-entrega do SQS ou retry), o MassTransit **não cria uma nova
instância de saga** porque a correlação pelo `CorrelationId` já encontra a instância existente.
Se a saga já transicionou para o próximo estado, o evento duplicado é ignorado silenciosamente
(o estado atual não corresponde ao handler que processaria aquele evento).

### Camada 2 — `AddEntityFrameworkOutbox` com `DuplicateDetectionWindow`

```csharp
// Program.cs / DI registration
services.AddMassTransit(cfg =>
{
    cfg.AddSagaStateMachine<OrderStateMachine, OrderSagaState>()
       .EntityFrameworkRepository(r =>
       {
           r.ConcurrencyMode = ConcurrencyMode.Optimistic;  // RowVersion
           r.AddDbContext<DbContext, SagaDbContext>((provider, options) =>
               options.UseNpgsql(connectionString));
       });

    cfg.AddEntityFrameworkOutbox<SagaDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();

        // Janela de deduplicação: mensagens idênticas dentro de 1 hora são descartadas
        o.DuplicateDetectionWindow = TimeSpan.FromHours(1);
    });

    cfg.UsingAmazonSqs((context, config) =>
    {
        config.Host("us-east-1", h =>
        {
            h.AccessKey("test");
            h.SecretKey("test");
            h.Config(new AmazonSQSConfig { ServiceURL = "http://localhost:4566" });
        });

        config.ConfigureEndpoints(context);
    });
});
```

### Consumers — zero código de idempotência

```csharp
// Antes (PaymentService/Worker.cs): ~40 linhas por handler incluindo verificação e gravação
// Depois: consumer limpo, sem qualquer lógica de idempotência manual

public class ProcessPaymentConsumer : IConsumer<ProcessPayment>
{
    public async Task Consume(ConsumeContext<ProcessPayment> context)
    {
        var command = context.Message;

        // Processar diretamente — sem verificação de idempotência manual
        var transactionId = Guid.NewGuid().ToString();

        await context.Publish(new PaymentCompleted
        {
            SagaId        = command.SagaId,
            TransactionId = transactionId
        });
    }
}
```

---

## Comparação Direta

| Aspecto | Atual | Com MassTransit |
|---|---|---|
| Tabela de idempotência | `idempotency_keys` criada via DDL manual | Não necessária — correlação do saga + Outbox |
| Verificação por consumer | `TryGetAsync<T>` antes de cada processamento (3 serviços × 2 handlers = ~6 blocos) | Ausente — transparente |
| Gravação por consumer | `SaveAsync<T>` após cada processamento | Ausente — Outbox garante exactly-once |
| Chave de idempotência | `{sagaId}-{step}` gerada manualmente no orquestrador | `DuplicateDetectionWindow` no Outbox |
| Re-entrega de reply | Reenvio manual do reply cacheado para a fila de replies | Ignorado automaticamente pelo state machine |
| Conexão PostgreSQL por consumer | Sim (cada serviço abre conexões para `idempotency_keys`) | Não necessária nos consumers |

**Estimativa: ~150 linhas eliminadas em 3 serviços.**

> Continua em [03 — Reply / Correlação →](./03-reply-correlation.md)
