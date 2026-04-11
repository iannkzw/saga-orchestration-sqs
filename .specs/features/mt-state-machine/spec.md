# Feature: mt-state-machine

**Milestone:** M10 - Migração MassTransit
**Status:** PLANNED

## Objetivo

Implementar a `OrderStateMachine` declarativa usando MassTransit Saga State Machine no `OrderService`. Substituir a state machine imperativa do `SagaOrchestrator` (switch/case manual + `SagaStateMachine.cs`) por uma definição declarativa que gerencia o happy path completo e a cadeia de compensação.

## Estados da Saga (MassTransit)

```
Initial → PaymentProcessing → InventoryReserving → ShippingScheduling → Final (Completed)
                  ↓                    ↓                    ↓
             Compensating ← ←  ←  ←  ←  ←  ←  ←  ←  ←  ←  ← ←
                  ↓
             Final (Failed)
```

Estados MassTransit usados:
- `Initial` — saga ainda não iniciada
- `PaymentProcessing` — aguardando resposta do PaymentService
- `InventoryReserving` — aguardando resposta do InventoryService
- `ShippingScheduling` — aguardando resposta do ShippingService
- `Compensating` — compensação em andamento
- `Final` — saga concluída (sucesso ou falha)

## Eventos que Acionam Transições

| Evento | Transição |
|--------|-----------|
| `OrderPlaced` | Initial → PaymentProcessing (publica `ProcessPayment`) |
| `PaymentCompleted` | PaymentProcessing → InventoryReserving (publica `ReserveInventory`) |
| `PaymentFailed` | PaymentProcessing → Final (Failed) |
| `InventoryReserved` | InventoryReserving → ShippingScheduling (publica `ScheduleShipping`) |
| `InventoryFailed` | InventoryReserving → Compensating (publica `CancelPayment`) |
| `ShippingScheduled` | ShippingScheduling → Final (Completed) |
| `ShippingFailed` | ShippingScheduling → Compensating (publica `CancelInventory`) |
| `PaymentCancelled` | Compensating → Final (Failed) |
| `InventoryCancelled` | Compensating → Final (Failed) ou publica `CancelPayment` |

## Modelo de Dados

### `OrderSagaInstance` (entidade MassTransit)

| Propriedade | Tipo | Descrição |
|-------------|------|-----------|
| `CorrelationId` | `Guid` | ID da saga (= OrderId) |
| `CurrentState` | `string` | Estado atual (gerenciado pelo MassTransit) |
| `OrderId` | `Guid` | Referência ao pedido |
| `CustomerId` | `string` | ID do cliente |
| `TotalAmount` | `decimal` | Valor total do pedido |
| `PaymentId` | `string?` | ID da transação de pagamento (preenchido pelo PaymentCompleted) |
| `ReservationId` | `string?` | ID da reserva de estoque |
| `CreatedAt` | `DateTime` | Data de criação |
| `UpdatedAt` | `DateTime` | Última atualização |

## Componentes

### 1. `OrderSagaInstance` (Models/)
Classe que implementa `SagaStateMachineInstance` do MassTransit. Mapeada para tabela `order_saga_instances` via EF Core.

### 2. `OrderStateMachine` (StateMachine/)
Classe que herda `MassTransitStateMachine<OrderSagaInstance>`. Define:
- Todos os `State` declarados como `State`
- Todos os `Event<T>` correlacionados por `CorrelationId`
- `Initially`, `During`, `DuringAny` com `When().TransitionTo().Then().PublishAsync()`

### 3. `OrderStateMachineDefinition` (StateMachine/)
Classe que herda `SagaDefinition<OrderSagaInstance>`. Configura endpoint name e concurrency.

## Decisões Técnicas

- **CorrelationId = OrderId:** A saga é correlacionada pelo ID do pedido para simplicidade
- **`Order.Status` atualizado via `.Then()`:** dentro do `Then()` block, atualizar `Order.Status` no `OrderDbContext` diretamente
- **Sem compensação em cascata complexa:** compensação simplificada — cancela na ordem inversa (Shipping → Inventory → Payment)
- **Estado `Compensating` unificado:** não há sub-estados de compensação; `CompensationStep` (string?) na instância indica o passo atual

## Critérios de Aceite

1. `OrderStateMachine` compila sem erros com MassTransit
2. Happy path completo: `OrderPlaced` → `PaymentCompleted` → `InventoryReserved` → `ShippingScheduled` → `Final`
3. Falha no Payment: `PaymentFailed` → `Final` (Failed), `Order.Status = Failed`
4. Falha no Inventory: `InventoryFailed` → `Compensating` → `PaymentCancelled` → `Final` (Failed)
5. Falha no Shipping: `ShippingFailed` → `Compensating` → `InventoryCancelled` → `PaymentCancelled` → `Final` (Failed)
6. `Order.Status` atualizado para `Completed` ou `Failed` ao chegar em `Final`
