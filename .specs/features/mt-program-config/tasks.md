# Tasks: mt-program-config

**Feature:** mt-program-config
**Milestone:** M10 - Migração MassTransit

## Resumo

5 tarefas, uma por serviço + uma de validação. Depende de todas as features de implementação: `mt-state-machine`, `mt-consumers`, `mt-messaging-infra`, `mt-outbox-dlq`, `mt-concurrency`.

---

## T1 — Configurar MassTransit no OrderService

**Arquivo:** `src/OrderService/Program.cs`

**O que fazer:**

Substituir todo o bloco de configuração de workers/HttpClient por `AddMassTransit` conforme spec.md (OrderService).

Remover:
- `AddHostedService<Worker>()` (se existir)
- `AddHttpClient` para SagaOrchestrator
- `services.AddSingleton<IIdempotencyStore, IdempotencyStore>()`

Adicionar:
- `AddMassTransit` com state machine, outbox, repositório EF, UsingAmazonSqs
- Manter `AddDbContext<OrderDbContext>` (necessário para o repositório e outbox)

**Verificação:** `docker compose up order-service` inicia sem erros. Log mostra `OrderStateMachine` e Outbox configurados.

---

## T2 — Configurar MassTransit no PaymentService

**Arquivo:** `src/PaymentService/Program.cs`

**O que fazer:**

Substituir `AddHostedService<Worker>()` por `AddMassTransit` com os consumidores de pagamento.

Remover:
- `AddHostedService<Worker>()`
- Configurações de fila SQS manual

Adicionar:
- `AddMassTransit` com `ProcessPaymentConsumer`, `CancelPaymentConsumer`, `UsingAmazonSqs`

**Verificação:** `docker compose up payment-service` inicia sem erros. Log mostra consumidores registrados.

---

## T3 — Configurar MassTransit no InventoryService

**Arquivo:** `src/InventoryService/Program.cs`

**O que fazer:**

Substituir `AddHostedService<Worker>()` por `AddMassTransit` com os consumidores de inventário.

Remover:
- `AddHostedService<Worker>()`
- Configurações de fila SQS manual

Adicionar:
- `AddMassTransit` com `ReserveInventoryConsumer`, `CancelInventoryConsumer`, `UsingAmazonSqs`
- Manter `IConfiguration` para leitura de `INVENTORY_LOCKING_MODE`

**Verificação:** `docker compose up inventory-service` inicia sem erros. Log mostra modo de locking ativo.

---

## T4 — Configurar MassTransit no ShippingService

**Arquivo:** `src/ShippingService/Program.cs`

**O que fazer:**

Substituir `AddHostedService<Worker>()` por `AddMassTransit` com o consumidor de shipping.

Remover:
- `AddHostedService<Worker>()`
- Configurações de fila SQS manual

Adicionar:
- `AddMassTransit` com `ScheduleShippingConsumer`, `UsingAmazonSqs`

**Verificação:** `docker compose up shipping-service` inicia sem erros.

---

## T5 — Validação end-to-end do fluxo completo

**O que fazer:**

1. `docker compose up` sobe todos os 4 serviços
2. `POST /orders` com payload válido
3. Aguardar processamento
4. `GET /orders/{id}` retorna `status: Completed`
5. Verificar logs de cada serviço mostrando o fluxo

**Verificação:** Fluxo completo Order → Payment → Inventory → Shipping → Completed funciona.

---

## Dependências

```
mt-state-machine → T1
mt-consumers → T1, T2, T3, T4
mt-messaging-infra → T1, T2, T3, T4
mt-outbox-dlq → T1
mt-concurrency → T1
mt-db-migration → T1
T1, T2, T3, T4 → T5 (todos os serviços configurados antes da validação)
```
