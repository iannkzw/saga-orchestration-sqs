# otel-traces — Design

## Arquitetura

```
OrderService          SagaOrchestrator         PaymentService
[HTTP Span]  ──HTTP──> [HTTP Span]
                       [Send Cmd Span] ──SQS──> [Process Cmd Span]
                                                 [Send Reply Span] ──SQS──>
                       [Process Reply Span]
                       [Send Cmd Span] ──SQS──> InventoryService ...
                       ...                       ShippingService ...
```

**Trace unico** conecta todo o fluxo via W3C TraceContext propagado nos SQS message attributes.

## Componentes

### C1 — Pacotes NuGet (Shared.csproj)

```xml
OpenTelemetry                                    1.15.0
OpenTelemetry.Api                                1.15.0
OpenTelemetry.Extensions.Hosting                 1.15.0
OpenTelemetry.Exporter.Console                   1.15.0
OpenTelemetry.Exporter.OpenTelemetryProtocol     1.15.0
OpenTelemetry.Instrumentation.AspNetCore         1.15.1
OpenTelemetry.Instrumentation.Http               1.15.1
```

### C2 — SqsTracePropagation (Shared/Telemetry/)

Helper centralizado para inject/extract de trace context em SQS message attributes.

```csharp
// Shared/Telemetry/SqsTracePropagation.cs
public static class SqsTracePropagation
{
    // Injeta traceparent + tracestate nos MessageAttributes do SendMessageRequest
    public static void Inject(IDictionary<string, MessageAttributeValue> attributes)

    // Extrai PropagationContext dos MessageAttributes recebidos
    public static PropagationContext Extract(IDictionary<string, MessageAttributeValue> attributes)
}
```

Usa `Propagators.DefaultTextMapPropagator` (W3C TraceContext) para inject/extract.

### C3 — SagaActivitySource (Shared/Telemetry/)

ActivitySource compartilhado + factory methods para spans padronizados.

```csharp
// Shared/Telemetry/SagaActivitySource.cs
public static class SagaActivitySource
{
    public static readonly ActivitySource Source = new("SagaOrchestration");

    // Cria span para envio de comando SQS
    public static Activity? StartSendCommand(string commandType, string sagaId)

    // Cria span para processamento de comando recebido
    public static Activity? StartProcessCommand(string commandType, string sagaId, PropagationContext parentContext)

    // Cria span para envio de reply SQS
    public static Activity? StartSendReply(string replyType, string sagaId)

    // Cria span para processamento de reply recebido
    public static Activity? StartProcessReply(string replyType, string sagaId, PropagationContext parentContext)
}
```

### C4 — AddSagaTracing() Extension (Shared/Extensions/)

Extension method no `ServiceCollectionExtensions.cs` para configurar OTel em todos os servicos.

```csharp
public static IServiceCollection AddSagaTracing(this IServiceCollection services, string serviceName)
{
    services.AddOpenTelemetry()
        .WithTracing(builder =>
        {
            builder
                .AddSource("SagaOrchestration")
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName))
                .AddAspNetCoreInstrumentation()   // HTTP incoming
                .AddHttpClientInstrumentation()    // HTTP outgoing
                .AddConsoleExporter();             // default

            // OTLP exporter se configurado
            var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
            if (!string.IsNullOrEmpty(otlpEndpoint))
                builder.AddOtlpExporter();
        });
    return services;
}
```

### C5 — Instrumentacao nos Workers

Cada Worker instrumenta:
1. **Receive loop**: extrai contexto do message attribute, cria span `process {CommandType}`
2. **Send command/reply**: cria span `send {Type}`, injeta contexto nos message attributes
3. **Tags**: `saga.id`, `saga.command_type`, `saga.state`

### C6 — Instrumentacao no SagaOrchestrator

O Worker do orquestrador instrumenta:
1. **Process reply**: extrai contexto, cria span com `saga.state`
2. **Send command**: cria span, injeta contexto
3. **State transitions**: registra estado como tag no span

## Fluxo de Propagacao

```
1. OrderService POST /orders
   └── [HTTP span automatico via AspNetCore instrumentation]
   └── HttpClient call to SagaOrchestrator
       └── [HTTP span automatico via HttpClient instrumentation]

2. SagaOrchestrator POST /sagas
   └── [HTTP span automatico]
   └── StartSendCommand("ProcessPayment", sagaId)
       └── SqsTracePropagation.Inject(messageAttributes)  // traceparent injected
       └── SQS SendMessageAsync

3. PaymentService Worker
   └── SQS ReceiveMessageAsync
   └── SqsTracePropagation.Extract(messageAttributes)     // traceparent extracted
   └── StartProcessCommand("ProcessPayment", sagaId, parentContext)
       └── [business logic]
       └── StartSendReply("PaymentReply", sagaId)
           └── SqsTracePropagation.Inject(messageAttributes)
           └── SQS SendMessageAsync

4. SagaOrchestrator Worker
   └── SQS ReceiveMessageAsync
   └── SqsTracePropagation.Extract(messageAttributes)
   └── StartProcessReply("PaymentReply", sagaId, parentContext)
       └── [state machine transition]
       └── StartSendCommand("ReserveInventory", sagaId)
           └── ... (repete ciclo)
```

## Decisoes

- **Console exporter como default**: minimo viavel, sem dependencia de infra adicional
- **OTLP opcional**: permite conectar Jaeger/Tempo depois sem mudar codigo
- **ActivitySource unica**: `SagaOrchestration` — simplifica filtragem e configuracao
- **Propagacao manual no SQS**: necessaria porque AWSSDK.SQS nao tem instrumentacao OTel nativa
- **Sem instrumentacao de DB**: EF Core/Npgsql OTel adiciona complexidade sem valor didatico significativo
