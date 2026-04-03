# otel-lgtm — Design

## Arquitetura

```
                          ┌──────────────────────┐
                          │   Grafana (:3000)     │
                          │  ┌───────┐ ┌───────┐ │
                          │  │ Tempo │ │ Loki  │ │
                          │  │traces │ │ logs  │ │
                          │  └───┬───┘ └───┬───┘ │
                          └──────┼─────────┼─────┘
                                 │  OTLPHTTP│
                          ┌──────┴─────────┴─────┐
                          │   OTel Collector      │
                          │  (:4317 gRPC)         │
                          │  (:4318 HTTP)         │
                          │                       │
                          │  tail_sampling:       │
                          │  - drop /health       │
                          │  - keep errors        │
                          │  - sample all default │
                          └──────────┬────────────┘
                                     │ OTLP gRPC
              ┌──────────┬───────────┼───────────┬──────────┐
              │          │           │           │          │
         OrderService  SagaOrch  PaymentSvc  InventorySvc  ShippingSvc
         (traces+logs) (traces+logs) ...       ...         ...
```

**Fluxo:**
1. Servicos .NET exportam traces e logs via OTLP gRPC para o Collector
2. Collector aplica sampling (drop health checks), batch, e encaminha para LGTM
3. LGTM armazena traces no Tempo e logs no Loki
4. Grafana visualiza com datasources provisionados

## Componentes

### C1 — Docker Compose: servicos LGTM + Collector

**Novos servicos no docker-compose.yml:**

```yaml
lgtm:
  image: grafana/otel-lgtm:latest
  ports:
    - "3000:3000"    # Grafana UI
  volumes:
    - ./infra/grafana/provisioning/datasources:/otel-lgtm/datasources
    - ./infra/grafana/provisioning/dashboards:/otel-lgtm/dashboards-config
    - ./infra/grafana/dashboards:/otel-lgtm/dashboards
  networks:
    - saga-network
  healthcheck:
    test: ["CMD-SHELL", "wget -qO- http://localhost:3000/api/health || exit 1"]
    interval: 10s
    timeout: 5s
    retries: 10

otelcol:
  image: otel/opentelemetry-collector-contrib:latest
  command: ["--config=/etc/otelcol/otelcol.yaml"]
  volumes:
    - ./infra/otel/otelcol.yaml:/etc/otelcol/otelcol.yaml:ro
    - ./infra/otel/processors/sampling:/etc/otelcol/processors/sampling:ro
  environment:
    - TRACES_URL=http://lgtm:4318/v1/traces
    - LOGS_URL=http://lgtm:4318/v1/logs
  ports:
    - "4317:4317"    # gRPC
    - "4318:4318"    # HTTP
  depends_on:
    lgtm:
      condition: service_healthy
  networks:
    - saga-network
  healthcheck:
    test: ["CMD-SHELL", "wget -qO- http://localhost:13133/health || exit 1"]
    interval: 10s
    timeout: 5s
    retries: 5
```

**Atualizacao dos servicos existentes:**
- Adicionar `OTEL_EXPORTER_OTLP_ENDPOINT: http://otelcol:4317` em todos os 5 servicos
- Adicionar `OTEL_SERVICE_NAME` por servico (order-service, saga-orchestrator, etc.)
- Adicionar `depends_on: otelcol: condition: service_started` nos servicos .NET

### C2 — OTel Collector Config (infra/otel/)

**infra/otel/otelcol.yaml:**

```yaml
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317
      http:
        endpoint: 0.0.0.0:4318

processors:
  memory_limiter:
    limit_mib: 512
    spike_limit_mib: 256
    check_interval: 5s

  tail_sampling:
    decision_wait: 5s
    num_traces: 5000
    policies: ${file:/etc/otelcol/processors/sampling/policies.yaml}

  batch:
    send_batch_size: 512
    timeout: 5s

exporters:
  otlphttp/traces:
    endpoint: ${env:TRACES_URL}
    tls:
      insecure: true
  otlphttp/logs:
    endpoint: ${env:LOGS_URL}
    tls:
      insecure: true
  debug:
    verbosity: basic

extensions:
  health_check:
    endpoint: 0.0.0.0:13133

service:
  extensions: [health_check]
  pipelines:
    traces:
      receivers: [otlp]
      processors: [memory_limiter, tail_sampling, batch]
      exporters: [otlphttp/traces, debug]
    logs:
      receivers: [otlp]
      processors: [memory_limiter, batch]
      exporters: [otlphttp/logs, debug]
```

**infra/otel/processors/sampling/policies.yaml:**
```yaml
- name: drop-health-checks
  type: and
  and:
    and_sub_policy:
      - name: health-path
        type: string_attribute
        string_attribute:
          key: url.path
          values: ["/health"]
      - name: get-method
        type: string_attribute
        string_attribute:
          key: http.request.method
          values: ["GET"]
      - name: status-ok
        type: status_code
        status_code:
          status_codes: [OK, UNSET]
    invert_match: true

- name: keep-errors
  type: status_code
  status_code:
    status_codes: [ERROR]

- name: sample-default
  type: always_sample
```

### C3 — AddSagaLogging() Extension (Shared/Extensions/)

Extensao do `ServiceCollectionExtensions.cs` existente:

```csharp
public static IServiceCollection AddSagaLogging(
    this IServiceCollection services,
    string serviceName)
{
    services.AddLogging(logging =>
    {
        logging.AddOpenTelemetry(options =>
        {
            options.SetResourceBuilder(
                ResourceBuilder.CreateDefault().AddService(serviceName));

            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;

            var otlpEndpoint = Environment.GetEnvironmentVariable(
                "OTEL_EXPORTER_OTLP_ENDPOINT");

            if (!string.IsNullOrEmpty(otlpEndpoint))
            {
                options.AddOtlpExporter(exporter =>
                {
                    exporter.Endpoint = new Uri(otlpEndpoint);
                    exporter.Protocol = OtlpExportProtocol.Grpc;
                });
            }
        });
    });

    return services;
}
```

**Pacote necessario:** ja incluido — `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.15.0

### C4 — Atualizacao AddSagaTracing()

Ajustes no extension method existente:
- Remover `AddConsoleExporter()` quando OTLP esta configurado (evitar duplicacao)
- Manter console exporter apenas quando OTLP nao esta configurado (dev local sem stack)

```csharp
public static IServiceCollection AddSagaTracing(
    this IServiceCollection services,
    string serviceName)
{
    services.AddOpenTelemetry()
        .WithTracing(builder =>
        {
            builder
                .AddSource(SagaActivitySource.Name)
                .SetResourceBuilder(
                    ResourceBuilder.CreateDefault().AddService(serviceName))
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation();

            var otlpEndpoint = Environment.GetEnvironmentVariable(
                "OTEL_EXPORTER_OTLP_ENDPOINT");

            if (!string.IsNullOrEmpty(otlpEndpoint))
                builder.AddOtlpExporter();
            else
                builder.AddConsoleExporter();
        });

    return services;
}
```

### C5 — Program.cs dos servicos

Adicionar `AddSagaLogging()` em todos os 5 servicos:

```csharp
// Existente:
builder.Services.AddSagaTracing("OrderService");

// Novo:
builder.Services.AddSagaLogging("OrderService");
```

Service names mapeados:
| Servico | Service Name |
|---------|-------------|
| OrderService | order-service |
| SagaOrchestrator | saga-orchestrator |
| PaymentService | payment-service |
| InventoryService | inventory-service |
| ShippingService | shipping-service |

**Nota:** Padronizar kebab-case nos service names para consistencia com env var `OTEL_SERVICE_NAME`.

### C6 — Grafana Provisioning (infra/grafana/)

**infra/grafana/provisioning/datasources/datasources.yaml:**
```yaml
apiVersion: 1
datasources:
  - name: Tempo
    type: tempo
    uid: tempo
    url: http://localhost:3200
    isDefault: false
    jsonData:
      tracesToLogsV2:
        datasourceUid: loki
        filterByTraceID: true
      nodeGraph:
        enabled: true

  - name: Loki
    type: loki
    uid: loki
    url: http://localhost:3100
    isDefault: false
    jsonData:
      derivedFields:
        - datasourceUid: tempo
          matcherRegex: "traceID=(\\w+)"
          name: TraceID
          url: "$${__value.raw}"
```

**infra/grafana/provisioning/dashboards/dashboards.yaml:**
```yaml
apiVersion: 1
providers:
  - name: Saga Orchestration
    orgId: 1
    folder: Saga Orchestration
    type: file
    disableDeletion: false
    allowUiUpdates: false
    updateIntervalSeconds: 30
    options:
      path: /otel-lgtm/dashboards
      foldersFromFilesStructure: false
```

**infra/grafana/dashboards/saga-overview.json:**
Dashboard com:
- Painel "Traces Recentes" (Tempo datasource — query por service.name)
- Painel "Logs por Servico" (Loki datasource — query por service_name)
- Variavel de template `$service` para filtrar por servico
- Link Tempo -> Logs correlacionados via TraceId

### C7 — Estrutura de diretórios novos

```
infra/
├── otel/
│   ├── otelcol.yaml
│   └── processors/
│       └── sampling/
│           └── policies.yaml
├── grafana/
│   ├── provisioning/
│   │   ├── datasources/
│   │   │   └── datasources.yaml
│   │   └── dashboards/
│   │       └── dashboards.yaml
│   └── dashboards/
│       └── saga-overview.json
```

## Decisoes

- **grafana/otel-lgtm imagem all-in-one:** Simplifica setup — Grafana + Tempo + Loki + Prometheus em um unico container. Adequado para PoC local.
- **OTel Collector como intermediario:** Permite tail sampling, batching e transformacao sem mudar codigo dos servicos. Padrao de producao.
- **Tail sampling no collector (nao nos servicos):** Decisao de sampling centralizada permite drop de health checks sem custo de processamento nos servicos.
- **ILogger + OpenTelemetry provider (sem Serilog):** Manter a simplicidade do ILogger existente. O provider OTel adiciona correlacao de trace automaticamente sem mudar os log statements existentes.
- **Console exporter condicional:** Manter para dev local sem stack, remover quando OTLP esta configurado para evitar duplicacao ruidosa.
- **Service names em kebab-case:** Consistencia com convencoes OTel e facilita queries no Grafana.
- **Sem metricas na V1:** Foco em traces + logs. Metricas serao feature separada para manter escopo controlado.
