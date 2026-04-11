# Tasks: mt-docs-didaticos

**Feature:** mt-docs-didaticos
**Milestone:** M10 - Migração MassTransit

## Resumo

10 tarefas (1 por documento). Todas independentes entre si. Deve ser executada após `mt-program-config` para que os snippets de código reflitam o código real implementado.

---

## T1 — Reescrever docs/01-visao-geral.md

**O que fazer:**

Atualizar diagrama de arquitetura para 4 serviços. Substituir descrição do `SagaOrchestrator` por `OrderService` com state machine embutida. Mencionar MassTransit como transporte.

---

## T2 — Reescrever docs/02-fluxo-happy-path.md

**O que fazer:**

Substituir diagrama de sequência command/reply por diagrama publish/subscribe MassTransit. Mostrar eventos (`OrderPlaced`, `PaymentCompleted`, etc.) em vez de comandos/replies. Atualizar passo a passo textual.

---

## T3 — Reescrever docs/03-compensacao.md

**O que fazer:**

Atualizar cadeia de compensação para a state machine declarativa. Mostrar como `During(Compensating)` trata os eventos de cancelamento. Diagrama com estados: `InventoryFailed` → `Compensating` → `PaymentCancelled` → `Final`.

---

## T4 — Reescrever docs/04-idempotencia.md

**O que fazer:**

Substituir explicação do `IdempotencyStore` pela abordagem MassTransit:
1. Correlação de saga (CorrelationId previne duplicatas na state machine)
2. `DuplicateDetectionWindow` no Outbox (deduplicação de mensagens)
3. Idempotência de negócio nos consumidores (verificação antes de processar)

---

## T5 — Reescrever docs/05-estado-saga.md

**O que fazer:**

Substituir descrição das tabelas `sagas` e `saga_state_transitions` pela tabela `order_saga_instances`. Explicar como o MassTransit persiste o estado via EF Core. Mostrar como `GET /orders/{id}` retorna o estado atual.

---

## T6 — Reescrever docs/06-dlq-visibilidade.md

**O que fazer:**

Atualizar nome das filas DLQ de `*-dlq` hardcodadas para `*_error` criadas pelo MassTransit. Atualizar endpoints `GET /dlq` e `POST /dlq/redrive`. Mencionar configuração de retry antes de ir para DLQ.

---

## T7 — Reescrever docs/07-concorrencia-sagas.md

**O que fazer:**

Manter seções de `pessimistic` e `optimistic` (da feature `optimistic-locking`). Adicionar seção sobre `ConcurrencyMode.Optimistic` do MassTransit na state machine (diferente do locking do InventoryService). Atualizar diagrama.

---

## T8 — Reescrever docs/08-observabilidade.md

**O que fazer:**

Manter conteúdo de OTel (feature `otel-lgtm`). Adicionar menção ao MassTransit: como o MassTransit propaga contexto de trace automaticamente via `Activity`. Verificar se instrumentação OTel ainda funciona com MassTransit.

---

## T9 — Criar docs/09-masstransit-state-machine.md

**O que fazer:**

Criar documento novo conforme spec.md. Estrutura:
1. State machine imperativa vs declarativa (comparativo com código antigo)
2. Anatomia da `MassTransitStateMachine<T>` (com snippets reais do `OrderStateMachine`)
3. Fluxo happy path no código
4. Fluxo de compensação no código
5. Como testar a state machine isoladamente

---

## T10 — Criar docs/10-outbox-pattern.md

**O que fazer:**

Criar documento novo conforme spec.md. Estrutura:
1. O problema do dual-write (com exemplo concreto de falha)
2. Solução: Transactional Outbox Pattern (diagrama)
3. MassTransit EF Core Outbox (configuração real do projeto)
4. Tabelas do Outbox (schema)
5. DuplicateDetectionWindow explicado
6. At-least-once delivery e como lidar

---

## Dependências

```
mt-program-config (implementação completa para snippets reais) → T1-T10
Todas as tarefas são independentes entre si — podem ser feitas em paralelo
```
