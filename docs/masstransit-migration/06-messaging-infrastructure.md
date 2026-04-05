# 06 — Infraestrutura de Mensageria

← [05 — Compensação](./05-compensation.md) | [Voltar ao índice](./00-overview.md) | Próximo: [07 — Transactional Outbox & DLQ →](./07-transactional-outbox-and-dlq.md)

---

## Implementação Atual

Toda a infraestrutura de mensageria é gerenciada manualmente: nomes de filas, resolução de URLs,
construção de requests SQS, serialização JSON, loop de polling e propagação de trace.

### `src/Shared/Configuration/SqsConfig.cs` — nomes de filas como constantes

```csharp
// src/Shared/Configuration/SqsConfig.cs
namespace Shared.Configuration;

public static class SqsConfig
{
    public const string OrderCommands    = "order-commands";
    public const string SagaCommands     = "saga-commands";

    public const string PaymentCommands  = "payment-commands";
    public const string PaymentReplies   = "payment-replies";

    public const string InventoryCommands = "inventory-commands";
    public const string InventoryReplies  = "inventory-replies";

    public const string ShippingCommands = "shipping-commands";
    public const string ShippingReplies  = "shipping-replies";

    public const string OrderStatusUpdates = "order-status-updates";

    // Nomes de todas as DLQs
    public static readonly string[] AllDlqNames =
    [
        $"{OrderCommands}-dlq",
        $"{SagaCommands}-dlq",
        $"{PaymentCommands}-dlq",
        $"{PaymentReplies}-dlq",
        $"{InventoryCommands}-dlq",
        $"{InventoryReplies}-dlq",
        $"{ShippingCommands}-dlq",
        $"{ShippingReplies}-dlq",
        $"{OrderStatusUpdates}-dlq"
    ];

    public static readonly Dictionary<string, string> DlqToOriginalQueue = AllDlqNames
        .ToDictionary(dlq => dlq, dlq => dlq[..^4]);
}
```

### Resolução manual de URLs (`GetQueueUrlAsync`)

Cada serviço resolve as URLs das suas filas no startup:

```csharp
// src/SagaOrchestrator/Worker.cs — ExecuteAsync
// Resolver URLs de todas as filas de reply no startup
var queueUrls = new Dictionary<string, string>();
foreach (var mapping in _replyQueues)
{
    var response = await _sqs.GetQueueUrlAsync(mapping.QueueName, stoppingToken);
    queueUrls[mapping.QueueName] = response.QueueUrl;
}

foreach (var queueName in _commandQueueNames)
{
    var r = await _sqs.GetQueueUrlAsync(queueName, stoppingToken);
    _commandQueueUrls[queueName] = r.QueueUrl;
}

var statusQueueResponse = await _sqs.GetQueueUrlAsync(SqsConfig.OrderStatusUpdates, stoppingToken);
_orderStatusQueueUrl = statusQueueResponse.QueueUrl;
```

### Construção manual de `ReceiveMessageRequest`

```csharp
// src/SagaOrchestrator/Worker.cs — ExecuteAsync (loop de polling)
var receiveResponse = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
{
    QueueUrl              = queueUrls[mapping.QueueName],
    MaxNumberOfMessages   = 10,
    WaitTimeSeconds       = 1,
    MessageAttributeNames = ["All"]
}, stoppingToken);
```

### Construção manual de `SendMessageRequest` com `MessageAttributes`

```csharp
// src/SagaOrchestrator/Worker.cs — SendCommandToQueueAsync
private async Task SendCommandToQueueAsync(object command, string commandQueue, string? simulateFailure, CancellationToken ct)
{
    var queueUrl    = _commandQueueUrls[commandQueue];
    var baseCommand = (BaseCommand)command;

    var request = new SendMessageRequest
    {
        QueueUrl    = queueUrl,
        MessageBody = JsonSerializer.Serialize(command, command.GetType()),  // serialização manual

        // MessageAttribute para distinguir tipo do comando
        MessageAttributes = new Dictionary<string, MessageAttributeValue>
        {
            ["CommandType"] = new()
            {
                DataType    = "String",
                StringValue = command.GetType().Name
            }
        }
    };

    if (!string.IsNullOrEmpty(simulateFailure))
    {
        request.MessageAttributes["SimulateFailure"] = new MessageAttributeValue
        {
            DataType    = "String",
            StringValue = simulateFailure
        };
    }

    // Injetar trace context manualmente nos MessageAttributes
    SqsTracePropagation.Inject(request.MessageAttributes);
    await _sqs.SendMessageAsync(request, ct);
}
```

### Desserialização manual por `CommandType` no consumer

```csharp
// src/PaymentService/Worker.cs — ExecuteAsync
foreach (var message in response?.Messages ?? [])
{
    // Ler atributo "CommandType" para saber qual tipo desserializar
    var commandType = message.MessageAttributes.TryGetValue("CommandType", out var attr)
        ? attr.StringValue
        : "ProcessPayment";

    if (commandType == nameof(RefundPayment))
        await HandleRefundPaymentAsync(message, replyQueueUrl, stoppingToken);
    else
        await HandleProcessPaymentAsync(message, replyQueueUrl, stoppingToken);

    await _sqs.DeleteMessageAsync(commandQueueUrl, message.ReceiptHandle, stoppingToken);
}
```

### `src/Shared/Telemetry/SqsTracePropagation.cs` — propagação de trace manual

```csharp
// src/Shared/Telemetry/SqsTracePropagation.cs
public static class SqsTracePropagation
{
    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    // Injetar trace context em MessageAttributes do SQS
    public static void Inject(IDictionary<string, MessageAttributeValue> attributes)
    {
        Propagator.Inject(
            new PropagationContext(Activity.Current?.Context ?? default, Baggage.Current),
            attributes,
            (attrs, key, value) =>
            {
                attrs[key] = new MessageAttributeValue
                {
                    DataType    = "String",
                    StringValue = value
                };
            });
    }

    // Extrair trace context de MessageAttributes recebidos
    public static PropagationContext Extract(IDictionary<string, MessageAttributeValue> attributes)
    {
        return Propagator.Extract(
            default,
            attributes,
            (attrs, key) =>
            {
                if (attrs.TryGetValue(key, out var attr) && attr.DataType == "String")
                    return [attr.StringValue];
                return [];
            });
    }
}
```

### `src/Shared/Extensions/ServiceCollectionExtensions.cs` — registro do `IAmazonSQS`

```csharp
// src/Shared/Extensions/ServiceCollectionExtensions.cs
public static IServiceCollection AddSagaConnectivity(this IServiceCollection services, string sqsServiceUrl)
{
    services.AddSingleton<IAmazonSQS>(_ => new AmazonSQSClient(new AmazonSQSConfig
    {
        ServiceURL = sqsServiceUrl
    }));
    services.AddSingleton<IdempotencyStore>();
    services.AddSingleton<SqsConnectivityCheck>();
    services.AddSingleton<PostgresConnectivityCheck>();
    services.AddSingleton<StartupConnectivityCheck>();
    services.AddHostedService(sp => sp.GetRequiredService<StartupConnectivityCheck>());
    return services;
}
```

**Arquivos envolvidos:**
- `src/Shared/Configuration/SqsConfig.cs`
- `src/Shared/Telemetry/SqsTracePropagation.cs`
- `src/Shared/Telemetry/SagaActivitySource.cs`
- `src/Shared/Extensions/ServiceCollectionExtensions.cs`
- `src/SagaOrchestrator/Worker.cs` (todo o polling + send logic)
- `src/PaymentService/Worker.cs`, `src/InventoryService/Worker.cs`, `src/ShippingService/Worker.cs`

---

## Com MassTransit

O transporte SQS é configurado uma única vez. A criação de filas, o loop de polling, a serialização
e o roteamento são gerenciados automaticamente.

### Configuração do transporte `UsingAmazonSqs`

```csharp
// Program.cs — configuração central do MassTransit com SQS
services.AddMassTransit(cfg =>
{
    // Registrar consumers e saga
    cfg.AddConsumer<ProcessPaymentConsumer>();
    cfg.AddConsumer<RefundPaymentConsumer>();
    cfg.AddConsumer<ReserveInventoryConsumer>();
    cfg.AddConsumer<ReleaseInventoryConsumer>();
    cfg.AddConsumer<ScheduleShippingConsumer>();
    cfg.AddConsumer<CancelShippingConsumer>();

    cfg.AddSagaStateMachine<OrderStateMachine, OrderSagaState>()
       .EntityFrameworkRepository(r =>
       {
           r.ConcurrencyMode = ConcurrencyMode.Optimistic;
           r.AddDbContext<DbContext, SagaDbContext>((provider, options) =>
               options.UseNpgsql(connectionString));
       });

    // Transporte SQS — substitui toda a infraestrutura manual
    cfg.UsingAmazonSqs((context, config) =>
    {
        config.Host("us-east-1", h =>
        {
            h.AccessKey("test");
            h.SecretKey("test");
            // Apontar para LocalStack em desenvolvimento
            h.Config(new AmazonSQSConfig { ServiceURL = "http://localhost:4566" });
        });

        // Criar e configurar todos os endpoints automaticamente
        // Sem GetQueueUrlAsync, sem SqsConfig, sem nomes manuais
        config.ConfigureEndpoints(context);
    });
});
```

### Consumer tipado — sem polling, sem MessageAttributes, sem delete manual

```csharp
// ProcessPaymentConsumer.cs — consumer limpo
public class ProcessPaymentConsumer : IConsumer<ProcessPayment>
{
    private readonly ILogger<ProcessPaymentConsumer> _logger;

    public ProcessPaymentConsumer(ILogger<ProcessPaymentConsumer> logger)
    {
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ProcessPayment> context)
    {
        var command = context.Message;

        _logger.LogInformation(
            "Processando pagamento: SagaId={SagaId}, OrderId={OrderId}, Amount={Amount}",
            command.SagaId, command.OrderId, command.Amount);

        // Lógica de negócio — sem código de infraestrutura SQS
        var transactionId = Guid.NewGuid().ToString();

        // Publicar evento — MassTransit serializa, roteia e gerencia a fila
        await context.Publish(new PaymentCompleted
        {
            SagaId        = command.SagaId,
            TransactionId = transactionId
        });

        // Sem DeleteMessageAsync — MassTransit faz automaticamente após Consume retornar
    }
}
```

### Trace — automático via OpenTelemetry instrumentation do MassTransit

```csharp
// Program.cs — apenas adicionar instrumentação, sem SqsTracePropagation manual
services.AddOpenTelemetry()
    .WithTracing(builder =>
    {
        builder
            .AddSource(SagaActivitySource.Name)
            .AddSource("MassTransit")  // MassTransit propaga trace automaticamente
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("saga-orchestrator"))
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();

        builder.AddOtlpExporter();
    });
```

---

## Comparação Direta

| Aspecto | Atual | Com MassTransit |
|---|---|---|
| Nomes de filas | `SqsConfig.cs` com constantes manuais | Gerados a partir dos nomes dos tipos de mensagem |
| Resolução de URL | `GetQueueUrlAsync` em cada serviço no startup | Ausente — gerenciado pelo transporte |
| Polling loop | `BackgroundService.ExecuteAsync` com `ReceiveMessageAsync` + `Task.Delay(500)` | Ausente — MassTransit gerencia workers internos |
| Delete de mensagem | `DeleteMessageAsync` após cada processamento | Ausente — automático após `Consume()` retornar sem exceção |
| Serialização | `JsonSerializer.Serialize(command, command.GetType())` manual | Automática — MassTransit usa `System.Text.Json` por padrão |
| Tipo do comando | `MessageAttributes["CommandType"]` + if/else por string | Tipo do consumer determinado pelo tipo genérico `IConsumer<T>` |
| Propagação de trace | `SqsTracePropagation.Inject/Extract` manual nos `MessageAttributes` | Automática via `AddSource("MassTransit")` |
| Configuração de DI | `AddSagaConnectivity` custom + `AddSingleton<IAmazonSQS>` | `AddMassTransit` + `UsingAmazonSqs` |
| Criação de filas | `infra/localstack/init-sqs.sh` manual | `ConfigureEndpoints(context)` automático |

**Estimativa: ~200 linhas eliminadas.**

> Continua em [07 — Transactional Outbox & DLQ →](./07-transactional-outbox-and-dlq.md)
