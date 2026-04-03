# otel-lgtm — Especificacao

**Feature:** Observabilidade com LGTM stack (Traces + Logs + Grafana)
**Milestone:** M8 - Observabilidade LGTM
**Escopo:** Large (infra + todos os servicos + Grafana provisioning)
**Referencia:** PoC otel-lgtm-dotnet-microservices (100% funcional)

---

## Objetivo

Integrar a stack LGTM (Loki, Grafana, Tempo, Mimir) ao projeto saga-orchestration para visualizar traces distribuidos e logs estruturados no Grafana. O repo ja possui OpenTelemetry SDK com traces (M4) — esta feature adiciona a infraestrutura de coleta (OTel Collector), exportacao de logs via OTel, e dashboards provisionados no Grafana.

## Contexto

O projeto ja tem:
- OpenTelemetry SDK 1.15.0 em todos os servicos (Shared.csproj)
- SagaActivitySource com spans customizados (send/process command/reply)
- SqsTracePropagation com W3C TraceContext via SQS message attributes
- Console exporter (default) + OTLP exporter (condicional via env var)
- AddSagaTracing() extension method em ServiceCollectionExtensions.cs
- ILogger<T> com console output (sem sink estruturado)

O que falta:
- Infraestrutura LGTM para receber e visualizar telemetria
- OTel Collector como intermediario entre servicos e backends
- Exportacao de logs via OpenTelemetry (ILogger -> OTLP -> Loki)
- Logs estruturados com correlacao TraceId/SpanId
- Dashboard Grafana provisionado para traces e logs

## Requisitos

### R1 — Infraestrutura LGTM no Docker Compose

- **R1.1:** Servico `lgtm` usando imagem `grafana/otel-lgtm:latest` com porta 3000 (Grafana UI)
- **R1.2:** Servico `otelcol` usando imagem `otel/opentelemetry-collector-contrib:latest` com portas 4317 (gRPC) e 4318 (HTTP)
- **R1.3:** Todos os 5 servicos .NET exportam telemetria para `otelcol` via OTLP gRPC
- **R1.4:** Collector encaminha traces para Tempo e logs para Loki (via LGTM)
- **R1.5:** Health checks configurados para lgtm e otelcol

### R2 — Configuracao do OTel Collector

- **R2.1:** Receiver OTLP (gRPC + HTTP)
- **R2.2:** Processor `batch` para agrupamento eficiente
- **R2.3:** Processor `memory_limiter` para protecao de memoria
- **R2.4:** Tail sampling: drop health checks (GET /health com status 200), keep errors, sample-all default
- **R2.5:** Exporter OTLPHTTP para LGTM (traces -> Tempo, logs -> Loki)
- **R2.6:** Pipelines separadas para traces e logs

### R3 — Exportacao de Logs via OpenTelemetry

- **R3.1:** Pacote `OpenTelemetry.Exporter.OpenTelemetryProtocol` ja existe — reutilizar para logs
- **R3.2:** Configurar `AddOpenTelemetry()` no logging provider com OTLP exporter
- **R3.3:** `IncludeFormattedMessage = true` e `IncludeScopes = true`
- **R3.4:** ResourceBuilder consistente entre tracing e logging (mesmo service name)
- **R3.5:** Extension method `AddSagaLogging()` em ServiceCollectionExtensions.cs
- **R3.6:** Todos os 5 servicos registram logging OTel no Program.cs

### R4 — Logs estruturados com correlacao de trace

- **R4.1:** Logs existentes ja usam ILogger com message templates — manter padrao
- **R4.2:** OpenTelemetry logging provider adiciona TraceId/SpanId automaticamente aos logs
- **R4.3:** Em pontos criticos (saga transitions, command processing), garantir que Activity.Current esta ativo para correlacao

### R5 — Grafana provisionado

- **R5.1:** Datasource Tempo provisionado para traces (via provisioning YAML)
- **R5.2:** Datasource Loki provisionado para logs (via provisioning YAML)
- **R5.3:** Dashboard provisionado com paineis basicos:
  - Trace explorer (busca por saga.id, service.name)
  - Log explorer (busca por service_name, correlacao com TraceId)
  - Tabela de traces recentes com duracao e status
- **R5.4:** Provisioning via volume mounts no docker-compose (padrao Grafana)

### R6 — Atualizacao da configuracao dos servicos

- **R6.1:** Variavel `OTEL_EXPORTER_OTLP_ENDPOINT=http://otelcol:4317` em todos os servicos no docker-compose
- **R6.2:** Variavel `OTEL_SERVICE_NAME` configurada por servico
- **R6.3:** Remover console exporter quando OTLP esta configurado (evitar poluicao de logs)

## Fora de Escopo (V1)

- Metricas (counters, histograms, gauges) — feature futura
- Alertas no Grafana — feature futura
- Instrumentacao EF Core/Npgsql — manter decisao do M4
- Exemplars (link metricas->traces) — depende de metricas
- Load generator / telemetrygen — desnecessario para PoC

## Criterios de Aceite

1. `docker compose up` sobe LGTM stack + Collector + todos os servicos
2. POST /orders gera trace visivel no Grafana/Tempo com spans de todos os servicos conectados
3. Logs dos 5 servicos aparecem no Grafana/Loki com correlacao de TraceId
4. Clicar em um trace no Tempo mostra logs correlacionados
5. Dashboard provisionado abre automaticamente no Grafana (http://localhost:3000)
6. Tail sampling filtra health checks — traces de GET /health nao aparecem no Tempo
