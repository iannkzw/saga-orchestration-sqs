# otel-traces — Especificacao

**Feature:** Traces distribuidos com OpenTelemetry
**Milestone:** M4 - Observabilidade e Documentacao
**Escopo:** Large (todos os servicos + Shared)

---

## Objetivo

Integrar OpenTelemetry SDK em todos os servicos da saga para que o fluxo completo (Order -> Payment -> Inventory -> Shipping) seja rastreavel como um unico trace distribuido. Propagacao de contexto via SQS message attributes usando W3C TraceContext.

## Requisitos

### R1 — OpenTelemetry SDK em todos os servicos

- **R1.1:** Pacotes OTel adicionados no Shared.csproj (dependencia transitiva para todos)
- **R1.2:** Configuracao centralizada de tracing no `ServiceCollectionExtensions.cs`
- **R1.3:** Cada servico registra OTel no seu `Program.cs` via extension method compartilhado

### R2 — Trace propagation via SQS message attributes

- **R2.1:** Ao enviar mensagem SQS, injetar `traceparent` e `tracestate` como message attributes
- **R2.2:** Ao receber mensagem SQS, extrair contexto de trace dos message attributes e criar span linkado
- **R2.3:** Helper centralizado no Shared para inject/extract (evitar duplicacao nos servicos)

### R3 — SagaId como tag/attribute nos spans

- **R3.1:** Todos os spans relacionados a uma saga incluem atributo `saga.id`
- **R3.2:** Spans de processamento de comando incluem `saga.command_type`
- **R3.3:** Spans do orquestrador incluem `saga.state` (estado atual da saga)

### R4 — Exporter configurado (minimo viavel)

- **R4.1:** Console exporter como default para desenvolvimento local
- **R4.2:** OTLP exporter configuravel via variavel de ambiente `OTEL_EXPORTER_OTLP_ENDPOINT`
- **R4.3:** Service name configurado por servico (`OrderService`, `SagaOrchestrator`, etc.)

### R5 — Instrumentacao automatica

- **R5.1:** Instrumentacao HTTP (AspNetCore) para OrderService e SagaOrchestrator (APIs)
- **R5.2:** Instrumentacao HttpClient para OrderService (chamadas ao orquestrador)

## Fora de Escopo

- Metricas (apenas traces nesta feature)
- Jaeger/Zipkin UI (pode ser adicionado depois)
- Instrumentacao de queries PostgreSQL/EF Core (complexidade adicional desnecessaria para PoC)

## Criterios de Aceite

1. `docker compose up` sobe todos os servicos com OTel configurado
2. POST /orders gera traces visiveis no console log com spans conectados
3. SagaId aparece como atributo em todos os spans
4. Trace context e propagado entre servicos via SQS message attributes
