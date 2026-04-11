# Tasks: mt-codebase-docs

**Feature:** mt-codebase-docs
**Milestone:** M10 - Migração MassTransit

## Resumo

7 tarefas, uma por documento. Todas independentes entre si. Deve ser executada após `mt-program-config` (estrutura final do código é a referência).

---

## T1 — Criar/atualizar ARCHITECTURE.md

**Arquivo:** `ARCHITECTURE.md` ou `docs/ARCHITECTURE.md`

**O que fazer:**

Criar documento com:
1. Diagrama de dependências entre projetos (`Shared` ← todos; `OrderService` tem state machine)
2. Fluxo de mensagens: lista de eventos e quem publica/consome cada um
3. Schema do banco: tabelas por serviço
4. Decisões arquiteturais chave (fusão OrderService+Orchestrator, MassTransit, Outbox)

---

## T2 — Criar src/OrderService/README.md

**Arquivo:** `src/OrderService/README.md`

**O que fazer:**

Documentar conforme padrão da spec.md:
- Responsabilidade: API REST + state machine da saga
- Estrutura de pastas
- Endpoints: `POST /orders`, `GET /orders/{id}`, `GET /dlq`, `POST /dlq/redrive`
- Variáveis de ambiente: `ConnectionStrings__DefaultConnection`, `AWS_SQS_SERVICE_URL`, etc.

---

## T3 — Criar src/PaymentService/README.md

**Arquivo:** `src/PaymentService/README.md`

**O que fazer:**

Documentar conforme padrão:
- Responsabilidade: processar e cancelar pagamentos
- Consumidores: `ProcessPaymentConsumer`, `CancelPaymentConsumer`
- Lógica simulada: quando aprova, quando rejeita
- Variáveis de ambiente: `AWS_SQS_SERVICE_URL`, etc.

---

## T4 — Criar src/InventoryService/README.md

**Arquivo:** `src/InventoryService/README.md`

**O que fazer:**

Documentar conforme padrão:
- Responsabilidade: reservar e cancelar reservas de estoque
- Consumidores: `ReserveInventoryConsumer`, `CancelInventoryConsumer`
- Endpoints: `POST /inventory/reset`, `GET /health`
- Modos de locking: `INVENTORY_LOCKING_MODE` (pessimistic, optimistic, none)
- Variáveis de ambiente

---

## T5 — Criar src/ShippingService/README.md

**Arquivo:** `src/ShippingService/README.md`

**O que fazer:**

Documentar conforme padrão:
- Responsabilidade: agendar entregas
- Consumidor: `ScheduleShippingConsumer`
- Lógica simulada: quando agenda, quando falha
- Variáveis de ambiente

---

## T6 — Criar src/Shared/README.md

**Arquivo:** `src/Shared/README.md`

**O que fazer:**

Documentar:
1. Contratos disponíveis: lista de eventos (9) e comandos (5) com namespace
2. Extension methods: `AddSagaTracing(serviceName)`, `ConfigureSqsHost(configuration)`
3. Helpers de telemetria: `SqsTracePropagation`, `SagaActivitySource`
4. Como adicionar um novo contrato (convenção de nomenclatura)

---

## T7 — Criar CONTRIBUTING.md

**Arquivo:** `CONTRIBUTING.md`

**O que fazer:**

Criar guia com:
1. Pré-requisitos: .NET 10, Docker, LocalStack
2. `docker compose up` para rodar localmente
3. Como adicionar um novo serviço de domínio (passo a passo)
4. Como adicionar um novo evento (contrato, consumidor, state machine)
5. Convenções: snake_case no DB, PascalCase no C#, pt-BR em docs
6. Como rodar os testes de integração

---

## Dependências

```
mt-program-config (estrutura final do código) → T1-T7
mt-cleanup (remoções concluídas) → T1-T7
Todas as tarefas são independentes entre si
```
