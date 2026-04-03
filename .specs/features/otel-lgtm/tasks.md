# otel-lgtm — Tasks

**Feature:** Observabilidade com LGTM stack (Traces + Logs + Grafana)
**Estimativa:** 10 tasks, 3 fases (infra → codigo → grafana)
**Dependencias entre fases:** Fase 2 depende de Fase 1, Fase 3 depende de Fase 2

---

## Fase 1 — Infraestrutura (Collector + LGTM)

### T1 — Criar configuracao do OTel Collector
**Arquivo:** `infra/otel/otelcol.yaml`
**Req:** R2.1, R2.2, R2.3, R2.5, R2.6
**Acao:**
- Criar `infra/otel/otelcol.yaml` com receivers (OTLP gRPC + HTTP), processors (memory_limiter, batch), exporters (otlphttp/traces, otlphttp/logs, debug), e pipelines (traces, logs)
- URLs dos exporters via env vars: `${env:TRACES_URL}`, `${env:LOGS_URL}`
- Extension health_check na porta 13133
**Verificacao:** Arquivo YAML valido com receivers, processors, exporters e service.pipelines definidos

### T2 — Criar politicas de tail sampling
**Arquivos:** `infra/otel/processors/sampling/policies.yaml`
**Req:** R2.4
**Acao:**
- Criar `infra/otel/processors/sampling/policies.yaml` com 3 politicas:
  1. `drop-health-checks`: AND (url.path=/health + GET + status OK/UNSET) com invert_match=true
  2. `keep-errors`: status_code ERROR -> always sample
  3. `sample-default`: always_sample (catch-all)
- Referenciar no otelcol.yaml via `${file:...}`
- Adicionar processor `tail_sampling` no otelcol.yaml (decision_wait=5s, num_traces=5000)
**Verificacao:** Politica drop-health-checks referenciada corretamente no otelcol.yaml

### T3 — Adicionar servicos lgtm e otelcol no Docker Compose
**Arquivo:** `docker-compose.yml`
**Req:** R1.1, R1.2, R1.5
**Acao:**
- Adicionar servico `lgtm` (grafana/otel-lgtm:latest, porta 3000, healthcheck wget /api/health)
- Adicionar servico `otelcol` (otel/opentelemetry-collector-contrib:latest, portas 4317/4318, volume configs, depends_on lgtm, healthcheck /health:13133)
- Volumes para configs do collector e grafana provisioning
- Ambos na rede saga-network
**Verificacao:** `docker compose config` valida sem erros

### T4 — Configurar env vars OTLP nos servicos .NET
**Arquivo:** `docker-compose.yml`
**Req:** R1.3, R6.1, R6.2
**Acao:**
- Adicionar em cada um dos 5 servicos .NET:
  - `OTEL_EXPORTER_OTLP_ENDPOINT: http://otelcol:4317`
  - `OTEL_SERVICE_NAME: {kebab-case-name}` (order-service, saga-orchestrator, payment-service, inventory-service, shipping-service)
- Adicionar `depends_on: otelcol: condition: service_started`
**Verificacao:** Cada servico tem OTEL_EXPORTER_OTLP_ENDPOINT e OTEL_SERVICE_NAME definidos

---

## Fase 2 — Codigo (Logging OTel + Ajuste Tracing)

### T5 — Criar AddSagaLogging() extension method
**Arquivo:** `src/Shared/Extensions/ServiceCollectionExtensions.cs`
**Req:** R3.1, R3.2, R3.3, R3.4, R3.5
**Acao:**
- Adicionar metodo `AddSagaLogging(this IServiceCollection services, string serviceName)` no ServiceCollectionExtensions.cs
- Configurar `AddLogging()` com `AddOpenTelemetry()`:
  - SetResourceBuilder com AddService(serviceName)
  - IncludeFormattedMessage = true
  - IncludeScopes = true
  - AddOtlpExporter condicional (se OTEL_EXPORTER_OTLP_ENDPOINT definido)
  - Protocol: gRPC
- Adicionar `using` necessarios para OpenTelemetry.Logs
**Verificacao:** Metodo compila sem erros, aceita serviceName como parametro

### T6 — Ajustar AddSagaTracing() — console exporter condicional
**Arquivo:** `src/Shared/Extensions/ServiceCollectionExtensions.cs`
**Req:** R6.3
**Acao:**
- Modificar AddSagaTracing para:
  - Se OTLP endpoint definido: usar AddOtlpExporter() (sem console)
  - Se OTLP endpoint nao definido: usar AddConsoleExporter() (fallback dev)
- Atualmente adiciona ambos — remover duplicacao
**Verificacao:** Com OTLP configurado, console exporter nao e adicionado

### T7 — Registrar AddSagaLogging() em todos os servicos
**Arquivos:** `src/OrderService/Program.cs`, `src/SagaOrchestrator/Program.cs`, `src/PaymentService/Program.cs`, `src/InventoryService/Program.cs`, `src/ShippingService/Program.cs`
**Req:** R3.6, R4.2
**Acao:**
- Adicionar `builder.Services.AddSagaLogging("service-name")` em cada Program.cs
- Usar mesmos service names kebab-case do docker-compose
- Posicionar logo apos a chamada AddSagaTracing() existente
**Verificacao:** Todos os 5 Program.cs contem chamada a AddSagaLogging()

### T8 — Padronizar service names para kebab-case
**Arquivos:** Todos os `Program.cs` (5 servicos)
**Req:** R6.2
**Acao:**
- Verificar e padronizar os nomes passados para AddSagaTracing():
  - "OrderService" → "order-service"
  - "SagaOrchestrator" → "saga-orchestrator"
  - "PaymentService" → "payment-service"
  - "InventoryService" → "inventory-service"
  - "ShippingService" → "shipping-service"
- Manter consistente entre AddSagaTracing() e AddSagaLogging()
**Verificacao:** Grep por AddSagaTracing/AddSagaLogging mostra kebab-case em todos os servicos

---

## Fase 3 — Grafana Provisioning

### T9 — Criar datasources e dashboard provisioning
**Arquivos:** `infra/grafana/provisioning/datasources/datasources.yaml`, `infra/grafana/provisioning/dashboards/dashboards.yaml`
**Req:** R5.1, R5.2, R5.4
**Acao:**
- Criar `infra/grafana/provisioning/datasources/datasources.yaml`:
  - Datasource Tempo (uid: tempo, url: http://localhost:3200, tracesToLogsV2 com link para Loki)
  - Datasource Loki (uid: loki, url: http://localhost:3100, derivedFields com link para Tempo via traceID)
- Criar `infra/grafana/provisioning/dashboards/dashboards.yaml`:
  - Provider "Saga Orchestration", folder: "Saga Orchestration", path: /otel-lgtm/dashboards
**Verificacao:** Arquivos YAML validos com datasources e provider configurados

### T10 — Criar dashboard JSON provisionado
**Arquivo:** `infra/grafana/dashboards/saga-overview.json`
**Req:** R5.3
**Acao:**
- Criar dashboard "Saga Orchestration - Overview" com:
  - Variavel template `$service` (valores: order-service, saga-orchestrator, payment-service, inventory-service, shipping-service)
  - Painel 1: "Traces Recentes" — Tempo query por service.name=$service, tabela com traceID, duration, status
  - Painel 2: "Logs" — Loki query `{service_name="$service"}`, log browser
  - Painel 3: "Trace por Saga ID" — Tempo query por saga.id (input manual)
- Refresh interval: 30s
- Timezone: browser default
**Verificacao:** JSON valido, importavel no Grafana, paineis renderizam com dados de teste

---

## Ordem de Execucao

```
Fase 1 (infra):     T1 → T2 → T3 → T4    (sequencial — T2 depende de T1, T3 depende de T1+T2)
Fase 2 (codigo):    T5 + T6 (paralelo) → T7 + T8 (paralelo)
Fase 3 (grafana):   T9 → T10              (sequencial — T10 depende do provider de T9)
```

**Fases 2 e 3 podem executar em paralelo** (nao dependem uma da outra, apenas de Fase 1).

## Verificacao Final (pos todas as tasks)

1. `docker compose up -d` — todos os servicos healthy
2. `curl -X POST http://localhost:5001/orders -H "Content-Type: application/json" -d '{"productId":"prod-1","quantity":1,"price":99.90}'`
3. Abrir http://localhost:3000 → Grafana
4. Navegar para Explore → Tempo → buscar traces recentes → trace com spans de todos os servicos
5. Navegar para Explore → Loki → buscar logs → filtrar por service_name → ver TraceId nos logs
6. Clicar em TraceId no log → navega para trace no Tempo
7. Verificar que traces de GET /health NAO aparecem no Tempo (tail sampling)
8. Abrir dashboard "Saga Orchestration - Overview" → paineis com dados
