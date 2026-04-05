# Guia de Migração: SQS Manual → MassTransit

Este guia documenta a migração da implementação atual de saga orquestrada (manual, via SQS) para MassTransit,
mapeando cada funcionalidade relevante com exemplos de código reais extraídos do projeto.

## Navegação

| # | Tópico | Arquivo |
|---|--------|---------|
| 01 | Controle de Status (State Machine) | [01-state-machine.md](./01-state-machine.md) |
| 02 | Idempotência | [02-idempotency.md](./02-idempotency.md) |
| 03 | Reply / Correlação de Respostas | [03-reply-correlation.md](./03-reply-correlation.md) |
| 04 | Concorrência | [04-concurrency.md](./04-concurrency.md) |
| 05 | Compensação | [05-compensation.md](./05-compensation.md) |
| 06 | Infraestrutura de Mensageria | [06-messaging-infrastructure.md](./06-messaging-infrastructure.md) |
| 07 | Transactional Outbox & DLQ | [07-transactional-outbox-and-dlq.md](./07-transactional-outbox-and-dlq.md) |
| 08 | Exemplo Completo | [08-full-example.md](./08-full-example.md) |

---

## Resumo Executivo

A implementação atual orquestra sagas de pedido usando filas SQS e código imperativo. Toda a lógica de
estado, correlação, idempotência, compensação e infraestrutura de mensageria é gerenciada manualmente.
O MassTransit oferece todas essas funcionalidades de forma declarativa e integrada, reduzindo
drasticamente o volume de código a manter.

---

## Tabela Consolidada de Comparação

| Funcionalidade | Implementação Atual (Manual/SQS) | Com MassTransit | Linhas Eliminadas (estimativa) |
|---|---|---|---|
| **State Machine** | `SagaStateMachine.cs` (classe estática, 3 dicionários), `SagaInstance.TransitionTo()`, lógica de branching em `Worker.cs` (`HandleSuccessAsync`, `HandleFailureAsync`, `HandleCompensationReplyAsync`) | `OrderStateMachine : MassTransitStateMachine<OrderSagaState>` declarativo com `Initially`/`During`/`When` | ~250 linhas |
| **Idempotência** | `IdempotencyStore.cs` com tabela PostgreSQL manual (`idempotency_keys`), verificação em cada consumer (PaymentService, InventoryService, ShippingService) | Correlação do repositório de saga previne reprocessamento + `AddEntityFrameworkOutbox` com `DuplicateDetectionWindow` | ~150 linhas (em 3 serviços) |
| **Reply / Correlação** | 3 filas de reply dedicadas (`payment-replies`, `inventory-replies`, `shipping-replies`), loop de polling em `Worker.ExecuteAsync`, `QueueMapping`, extração manual de `SagaId` via `JsonElement`, `FirstOrDefaultAsync` | `CorrelateById` nos eventos; consumers fazem `Publish()` de eventos tipados; MassTransit roteia automaticamente | ~120 linhas + 9 filas a menos |
| **Concorrência** | Ausente — reconhecida como dívida técnica em comentários TODO no código | `RowVersion` na saga state + `IsRowVersion()` no EF Core + retry automático em `DbUpdateConcurrencyException` | ~50 linhas adicionadas (resolve dívida) |
| **Compensação** | `StoreCompensationData()`, `CompensationDataJson` (dicionário serializado como string JSON), `GetCompensationData()`, `SendCompensationCommandAsync()`, switch em `saga.CurrentState` | Dados de compensação como propriedades tipadas na saga state; compensação é outro bloco `During(State, When(Event).Send(...).TransitionTo(...))` | ~130 linhas |
| **Infraestrutura de Mensageria** | `SqsConfig.cs` com nomes de filas, resolução manual de URLs via `GetQueueUrlAsync`, construção de `SendMessageRequest`/`ReceiveMessageRequest`, `DeleteMessageAsync`, `MessageAttributes` para `CommandType`, serialização JSON manual, `BackgroundService` com loop de polling, propagação de trace via `SqsTracePropagation` | `UsingAmazonSqs` no transporte, `ConfigureEndpoints(context)` para criação automática de filas, consumers tipados com `IConsumer<T>`, serialização automática | ~200 linhas |
| **Transactional Outbox** | Ausente — TODO comentado: *"salvar comando na mesma tx do DB e publicar via job separado para garantir entrega exactly-once sem dual-write"* | `AddEntityFrameworkOutbox` salva mensagens na mesma transação do banco de dados | Resolve dívida técnica |
| **DLQ / Redrive** | Endpoints manuais `/dlq` e `/dlq/redrive` (~90 linhas em `Program.cs`) | Filas de erro e dead-letter built-in com políticas de retry configuráveis | ~90 linhas |
| **TOTAL ESTIMADO** | | | **~900+ linhas** |

---

## Arquitetura Atual vs. Proposta

### Atual (Manual SQS)

```
OrderService ──POST /sagas──► SagaOrchestrator
                                    │
                     ┌──────────────┼──────────────┐
                     ▼              ▼               ▼
              payment-commands  inventory-commands  shipping-commands
                     │              │               │
              PaymentService  InventoryService  ShippingService
                     │              │               │
                     ▼              ▼               ▼
              payment-replies  inventory-replies  shipping-replies
                     └──────────────┼──────────────┘
                                    ▼
                          SagaOrchestrator (polling)
                          └── HandleSuccessAsync / HandleFailureAsync
                              └── SagaStateMachine (dicionários)
                              └── StoreCompensationData
                              └── SendForwardCommandAsync / SendCompensationCommandAsync
```

### Com MassTransit

```
OrderService ──Publish<CreateOrder>──► MassTransit Bus
                                             │
                              OrderStateMachine (declarativo)
                              ├── Initially: ProcessPayment → PaymentProcessing
                              ├── During(PaymentProcessing):
                              │   ├── PaymentCompleted → InventoryReserving (Send ReserveInventory)
                              │   └── PaymentFailed    → PaymentRefunding (Failed direto)
                              ├── During(InventoryReserving): ...
                              ├── During(ShippingScheduling): ...
                              └── Compensation chain declarativa
                                             │
                    ProcessPaymentConsumer  ReserveInventoryConsumer  ScheduleShippingConsumer
                    (IConsumer<ProcessPayment>)       ...                      ...
                         └── Publish<PaymentCompleted> ou Publish<PaymentFailed>
```

---

## Benefícios Quantificados

- **~900+ linhas de código eliminadas**
- **9 filas SQS a menos** (as 3 filas de reply + suas 3 DLQs, mais as DLQs das filas de command que são gerenciadas automaticamente)
- **0 código de polling manual** — MassTransit gerencia o consumer loop
- **Concorrência resolvida** — dívida técnica eliminada
- **Dual-write eliminado** — Transactional Outbox integrado
- **Idempotência built-in** — via correlação do saga repository

> Veja o exemplo completo em [08-full-example.md](./08-full-example.md).
