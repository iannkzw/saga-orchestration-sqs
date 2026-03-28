# Saga Orchestration .NET + SQS

**Vision:** PoC didatica que demonstra o padrao Saga Orquestrada em um fluxo de e-commerce simplificado (Order -> Payment -> Inventory -> Shipping) usando .NET 10 e Amazon SQS via LocalStack, com compensacoes automaticas, idempotencia e observabilidade.

**Para:** Engenheiros backend e arquitetos que querem entender na pratica como implementar saga orquestrada com .NET e SQS.

**Resolve:** O padrao Saga e amplamente discutido na teoria mas raramente demonstrado de forma completa e reproduzivel. Esta PoC oferece um exemplo funcional end-to-end com infraestrutura local.

## Goals

- Demonstrar saga orquestrada completa com maquina de estados, compensacoes em cascata e idempotencia — validado por cenarios de teste reproduziveis
- Servir como material didatico em portugues sobre padroes de arquitetura distribuida — medido pela completude da documentacao em docs/
- Ser 100% reproduzivel localmente com um unico `docker compose up` — sem dependencia de conta AWS

## Tech Stack

**Core:**

- Framework: ASP.NET Core 10 (Minimal API + Worker Services)
- Language: C# / .NET 10
- Database: PostgreSQL
- Messaging: Amazon SQS via LocalStack

**Key dependencies:**

- AWSSDK.SQS (client SQS)
- Npgsql / EF Core (acesso PostgreSQL)
- OpenTelemetry SDK (traces distribuidos)
- Docker Compose (orquestracao de containers)
- LocalStack (emulacao AWS local)

## Scope

**v1 includes:**

- Saga Orchestrator com maquina de estados (Pending -> Completed / Failed)
- 4 servicos (Order, Payment, Inventory, Shipping) comunicando via SQS
- Compensacao em cascata automatica quando um passo falha
- Idempotency key em todos os handlers
- Dead Letter Queues com visibilidade
- Correlation ID (SagaId) em todas as mensagens
- Infraestrutura local completa (Docker Compose + LocalStack + PostgreSQL)
- Documentacao didatica em portugues

**Explicitly out of scope:**

- Autenticacao/autorizacao
- UI/frontend
- Deploy em cloud real (AWS)
- Saga coreografada (apenas orquestrada)
- Performance tuning / load testing
- Multiplas sagas concorrentes sobre o mesmo recurso (movido para M5 como exemplo didatico)

## Constraints

- Technical: Tudo roda local via Docker Compose + LocalStack, sem conta AWS
- Resources: PoC individual para aprendizado, construcao incremental por milestones
- Quality: Codigo didatico — clareza sobre otimizacao
