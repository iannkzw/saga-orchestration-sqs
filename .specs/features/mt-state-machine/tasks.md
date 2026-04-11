# Tasks: mt-state-machine

**Feature:** mt-state-machine
**Milestone:** M10 - Migração MassTransit

## Resumo

6 tarefas. Depende de `mt-event-contracts` (eventos precisam existir) e `mt-db-migration` (tabela `order_saga_instances`).

---

## T1 — Criar `OrderSagaInstance`

**Arquivo:** `src/OrderService/Models/OrderSagaInstance.cs`

**O que fazer:**

Criar classe com propriedades:
- `Guid CorrelationId` (implementa `SagaStateMachineInstance`)
- `string CurrentState`
- `Guid OrderId`
- `string CustomerId`
- `decimal TotalAmount`
- `string? PaymentId`
- `string? ReservationId`
- `string? CompensationStep`
- `string? FailureReason`
- `DateTime CreatedAt`
- `DateTime UpdatedAt`

**Verificação:** Classe compila com `using MassTransit;`.

---

## T2 — Configurar mapeamento EF Core para `OrderSagaInstance`

**Arquivo:** `src/OrderService/Data/OrderDbContext.cs`

**O que fazer:**

Adicionar `DbSet<OrderSagaInstance> SagaInstances` e configurar mapeamento em `OnModelCreating` (snake_case, tabela `order_saga_instances`, PK em `CorrelationId`).

**Verificação:** `dotnet ef migrations add` gera migration com tabela `order_saga_instances`.

---

## T3 — Implementar `OrderStateMachine` (happy path)

**Arquivo:** `src/OrderService/StateMachine/OrderStateMachine.cs`

**O que fazer:**

1. Declarar estados: `PaymentProcessing`, `InventoryReserving`, `ShippingScheduling`
2. Declarar eventos: `OrderPlaced`, `PaymentCompleted`, `InventoryReserved`, `ShippingScheduled`
3. Configurar correlações por `OrderId`
4. Implementar fluxo happy path completo (see design.md)
5. No `TransitionTo(Final)` de sucesso: `instance.UpdatedAt = DateTime.UtcNow`

**Verificação:** Classe compila. Fluxo happy path transitável via teste unitário.

---

## T4 — Implementar compensação na `OrderStateMachine`

**Arquivo:** `src/OrderService/StateMachine/OrderStateMachine.cs`

**O que fazer:**

1. Adicionar estado `Compensating`
2. Declarar eventos: `PaymentFailed`, `InventoryFailed`, `ShippingFailed`, `PaymentCancelled`, `InventoryCancelled`
3. Implementar cadeia de compensação (ver design.md D3)
4. No `TransitionTo(Final)` de falha: setar `instance.FailureReason` e `instance.UpdatedAt`

**Verificação:** Cenários de falha transitáveis — PaymentFailed vai para Final, InventoryFailed vai para Compensating e então Final.

---

## T5 — Criar `UpdateOrderStatusActivity`

**Arquivo:** `src/OrderService/StateMachine/UpdateOrderStatusActivity.cs`

**O que fazer:**

Criar `IStateMachineActivity<OrderSagaInstance>` que:
1. Recebe `IOrderRepository` via construtor
2. Em `Execute()`: atualiza `Order.Status` para `Completed` ou `Failed` baseado no estado atual da instância
3. Em `Faulted()`: log de erro, sem lançar exceção

Usar essa atividade nos `TransitionTo(Final)` tanto do happy path quanto da compensação.

**Verificação:** Atividade compila e é registrada no DI.

---

## T6 — Criar `OrderStateMachineDefinition`

**Arquivo:** `src/OrderService/StateMachine/OrderStateMachineDefinition.cs`

**O que fazer:**

Criar classe herdando `SagaDefinition<OrderSagaInstance>`:
- `EndpointName = "order-saga"`
- Configurar `ConcurrentMessageLimit = 8` (inicial conservador)

**Verificação:** Classe compila e é registrada no MassTransit via `AddSagaStateMachine<OrderStateMachine, OrderSagaInstance, OrderStateMachineDefinition>()`.

---

## Dependências

```
mt-event-contracts (eventos precisam existir) → T3, T4
mt-db-migration (tabela criada) → T2
T1 → T2, T3, T4
T3 → T4
T4 → T5
T5, T6 → registrar em mt-program-config
```
