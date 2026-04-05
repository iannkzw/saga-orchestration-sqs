# 03 — Reply / Correlação de Respostas

← [02 — Idempotência](./02-idempotency.md) | [Voltar ao índice](./00-overview.md) | Próximo: [04 — Concorrência →](./04-concurrency.md)

---

## Implementação Atual

Cada serviço downstream possui uma fila de reply dedicada. O orquestrador mantém um loop de polling
manual que lê todas as filas de reply, extrai o `SagaId` via `JsonElement` e localiza a instância
correta no banco de dados.

### `src/Shared/Configuration/SqsConfig.cs` — 9 filas (6 para replies + suas DLQs)

```csharp
// src/Shared/Configuration/SqsConfig.cs
public static class SqsConfig
{
    public const string PaymentCommands  = "payment-commands";
    public const string PaymentReplies   = "payment-replies";    // reply dedicada

    public const string InventoryCommands = "inventory-commands";
    public const string InventoryReplies  = "inventory-replies"; // reply dedicada

    public const string ShippingCommands = "shipping-commands";
    public const string ShippingReplies  = "shipping-replies";   // reply dedicada

    public const string OrderStatusUpdates = "order-status-updates";

    // 9 DLQs: uma para cada fila acima
    public static readonly string[] AllDlqNames =
    [
        $"{OrderCommands}-dlq",
        $"{SagaCommands}-dlq",
        $"{PaymentCommands}-dlq",
        $"{PaymentReplies}-dlq",     // DLQ da fila de reply do pagamento
        $"{InventoryCommands}-dlq",
        $"{InventoryReplies}-dlq",   // DLQ da fila de reply do estoque
        $"{ShippingCommands}-dlq",
        $"{ShippingReplies}-dlq",    // DLQ da fila de reply do frete
        $"{OrderStatusUpdates}-dlq"
    ];
}
```

### `src/SagaOrchestrator/Worker.cs` — QueueMapping e loop de polling

```csharp
// src/SagaOrchestrator/Worker.cs

// Struct que mapeia fila de reply para um nome de tipo legível
private readonly record struct QueueMapping(string QueueName, string ReplyTypeName);

// 3 filas monitoradas pelo orquestrador
private static readonly QueueMapping[] _replyQueues =
[
    new(SqsConfig.PaymentReplies,   "PaymentReplies"),
    new(SqsConfig.InventoryReplies, "InventoryReplies"),
    new(SqsConfig.ShippingReplies,  "ShippingReplies"),
];

protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    // Resolver URLs de todas as filas de reply no startup
    var queueUrls = new Dictionary<string, string>();
    foreach (var mapping in _replyQueues)
    {
        var response = await _sqs.GetQueueUrlAsync(mapping.QueueName, stoppingToken);
        queueUrls[mapping.QueueName] = response.QueueUrl;
    }

    // Loop de polling infinito — itera por todas as filas de reply
    while (!stoppingToken.IsCancellationRequested)
    {
        foreach (var mapping in _replyQueues)
        {
            var receiveResponse = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl            = queueUrls[mapping.QueueName],
                MaxNumberOfMessages = 10,
                WaitTimeSeconds     = 1,
                MessageAttributeNames = ["All"]
            }, stoppingToken);

            foreach (var message in receiveResponse?.Messages ?? [])
            {
                await ProcessReplyAsync(message, mapping, queueUrls[mapping.QueueName], stoppingToken);
            }
        }

        await Task.Delay(500, stoppingToken);
    }
}
```

### `src/SagaOrchestrator/Worker.cs` — Extração manual do SagaId e lookup no banco

```csharp
// src/SagaOrchestrator/Worker.cs — ProcessReplyAsync
private async Task ProcessReplyAsync(
    Message message,
    QueueMapping mapping,
    string queueUrl,
    CancellationToken ct)
{
    // ① Deserializar apenas os campos base para obter SagaId e Success
    var baseReply = JsonSerializer.Deserialize<JsonElement>(message.Body);

    // ② Validar presença dos campos obrigatórios
    if (!baseReply.TryGetProperty("SagaId", out var sagaIdProp) ||
        !baseReply.TryGetProperty("Success", out var successProp))
    {
        _logger.LogError("Mensagem malformada em {Queue}: {Body}", mapping.QueueName, message.Body);
        await _sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, ct);
        return;
    }

    // ③ Extrair SagaId manualmente do JsonElement
    var sagaId  = sagaIdProp.GetGuid();
    var success = successProp.GetBoolean();

    // ④ Lookup no banco — FirstOrDefaultAsync pelo SagaId
    using var scope = _scopeFactory.CreateScope();
    var db   = scope.ServiceProvider.GetRequiredService<SagaDbContext>();
    var saga = await db.Sagas.FirstOrDefaultAsync(s => s.Id == sagaId, ct);

    if (saga is null)
    {
        _logger.LogWarning("Saga nao encontrada: {SagaId}", sagaId);
        await _sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, ct);
        return;
    }

    // ⑤ Rotear para o handler correto
    if (SagaStateMachine.IsCompensating(saga.CurrentState))
        await HandleCompensationReplyAsync(saga, success, mapping, db, ct);
    else if (!success)
        await HandleFailureAsync(saga, baseReply, mapping, db, ct);
    else
        await HandleSuccessAsync(saga, baseReply, mapping, db, ct);

    // ⑥ Deletar mensagem da fila após processamento
    await _sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, ct);
}
```

### Services — enviando replies para filas dedicadas

```csharp
// src/PaymentService/Worker.cs — HandleProcessPaymentAsync
// Após processar, envia reply para a fila "payment-replies" manualmente
var replyRequest = new SendMessageRequest
{
    QueueUrl    = replyQueueUrl,                        // URL da fila "payment-replies"
    MessageBody = JsonSerializer.Serialize(reply),
    MessageAttributes = new Dictionary<string, MessageAttributeValue>()
};
SqsTracePropagation.Inject(replyRequest.MessageAttributes);
await _sqs.SendMessageAsync(replyRequest, ct);
```

O mesmo padrão se repete em InventoryService e ShippingService.

**Arquivos envolvidos:**
- `src/Shared/Configuration/SqsConfig.cs` (3 filas de reply, 3 DLQs de reply)
- `src/SagaOrchestrator/Worker.cs` (`QueueMapping`, `ExecuteAsync` polling, `ProcessReplyAsync`)
- `src/PaymentService/Worker.cs` (envio para `payment-replies`)
- `src/InventoryService/Worker.cs` (envio para `inventory-replies`)
- `src/ShippingService/Worker.cs` (envio para `shipping-replies`)

---

## Com MassTransit

Não existem filas de reply. Os consumers publicam eventos tipados diretamente no bus, e o MassTransit
roteia automaticamente para a instância de saga correta usando `CorrelateById`.

### Correlação declarativa na state machine

```csharp
// OrderStateMachine.cs — correlações declaradas uma única vez
Event(() => PaymentCompleted,
    x => x.CorrelateById(ctx => ctx.Message.SagaId));

Event(() => PaymentFailed,
    x => x.CorrelateById(ctx => ctx.Message.SagaId));

Event(() => InventoryReserved,
    x => x.CorrelateById(ctx => ctx.Message.SagaId));

// ... demais eventos correlacionados da mesma forma
```

### Consumer — apenas publica evento, sem saber de filas

```csharp
// ProcessPaymentConsumer.cs
public class ProcessPaymentConsumer : IConsumer<ProcessPayment>
{
    public async Task Consume(ConsumeContext<ProcessPayment> context)
    {
        var command = context.Message;

        // Processar pagamento...
        var success       = true; // lógica real aqui
        var transactionId = Guid.NewGuid().ToString();

        if (success)
        {
            // Publicar evento de sucesso — MassTransit roteia para a saga automaticamente
            await context.Publish(new PaymentCompleted
            {
                SagaId        = command.SagaId,
                TransactionId = transactionId
            });
        }
        else
        {
            await context.Publish(new PaymentFailed
            {
                SagaId       = command.SagaId,
                ErrorMessage = "Falha no processamento do pagamento"
            });
        }
    }
}
```

### Registro — sem URLs de filas, sem polling manual

```csharp
// Program.cs
services.AddMassTransit(cfg =>
{
    // Registrar todos os consumers
    cfg.AddConsumer<ProcessPaymentConsumer>();
    cfg.AddConsumer<RefundPaymentConsumer>();
    cfg.AddConsumer<ReserveInventoryConsumer>();
    cfg.AddConsumer<ReleaseInventoryConsumer>();
    cfg.AddConsumer<ScheduleShippingConsumer>();
    cfg.AddConsumer<CancelShippingConsumer>();

    // Registrar a saga
    cfg.AddSagaStateMachine<OrderStateMachine, OrderSagaState>()
       .EntityFrameworkRepository(r => { ... });

    cfg.UsingAmazonSqs((context, config) =>
    {
        config.Host("us-east-1", h => { ... });

        // Cria e configura todos os endpoints automaticamente
        // Não é necessário listar filas manualmente
        config.ConfigureEndpoints(context);
    });
});
```

---

## Comparação Direta

| Aspecto | Atual | Com MassTransit |
|---|---|---|
| Filas de reply | 3 filas dedicadas (`payment-replies`, `inventory-replies`, `shipping-replies`) | Nenhuma — eventos publicados no bus |
| DLQs de reply | 3 DLQs adicionais | Gerenciadas automaticamente pelo MassTransit |
| Polling manual | Loop em `ExecuteAsync` iterando por 3 filas a cada 500ms | Ausente — MassTransit gerencia consumers |
| Extração de SagaId | `JsonElement.TryGetProperty("SagaId", ...)` manual | `CorrelateById` declarativo |
| Lookup de saga | `db.Sagas.FirstOrDefaultAsync(s => s.Id == sagaId)` | MassTransit usa o saga repository automaticamente |
| Roteamento de reply | Manual (`QueueMapping` + switch de estado) | Automático via correlação |
| Envio de reply nos services | `SendMessageAsync` para URL da fila de reply | `Publish<TEvent>()` — sem URL, sem fila |
| Total de filas SQS | 9 filas principais + 9 DLQs = 18 filas | ~3 filas principais + tratamento automático de erros |

**Estimativa: ~120 linhas eliminadas + 9 filas a menos.**

> Continua em [04 — Concorrência →](./04-concurrency.md)
