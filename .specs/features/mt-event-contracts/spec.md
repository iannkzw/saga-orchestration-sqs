# Feature: mt-event-contracts

**Milestone:** M10 - Migração MassTransit
**Status:** PLANNED

## Objetivo

Definir os contratos de eventos e comandos tipados no projeto `Shared`, substituindo os tipos `*Command` e `*Reply` existentes por interfaces e records MassTransit compatíveis. Todos os serviços referenciam esses contratos via `ProjectReference` no `Shared.csproj`.

## Contratos Atuais (a remover)

Os contratos existentes no `Shared` usam o padrão command/reply com `Success: bool` na reply. Exemplos:
- `ProcessPaymentCommand`, `PaymentReply`
- `ReserveInventoryCommand`, `InventoryReply`
- `ScheduleShippingCommand`, `ShippingReply`
- `CancelPaymentCommand`, `CancelInventoryCommand`

## Contratos Novos (MassTransit)

### Eventos publicados pelo OrderService (saga outbox)

| Contrato | Publicado por | Consumido por |
|----------|--------------|--------------|
| `OrderPlaced` | OrderService | OrderStateMachine |
| `ProcessPayment` | OrderStateMachine | PaymentService |
| `ReserveInventory` | OrderStateMachine | InventoryService |
| `ScheduleShipping` | OrderStateMachine | ShippingService |
| `CancelPayment` | OrderStateMachine | PaymentService |
| `CancelInventory` | OrderStateMachine | InventoryService |

### Eventos publicados pelos serviços de domínio

| Contrato | Publicado por | Consumido por |
|----------|--------------|--------------|
| `PaymentCompleted` | PaymentService | OrderStateMachine |
| `PaymentFailed` | PaymentService | OrderStateMachine |
| `PaymentCancelled` | PaymentService | OrderStateMachine |
| `InventoryReserved` | InventoryService | OrderStateMachine |
| `InventoryFailed` | InventoryService | OrderStateMachine |
| `InventoryCancelled` | InventoryService | OrderStateMachine |
| `ShippingScheduled` | ShippingService | OrderStateMachine |
| `ShippingFailed` | ShippingService | OrderStateMachine |

## Estrutura dos Contratos

Todos os contratos são `record` imutáveis com `Guid CorrelationId` (= OrderId). Sem herança — MassTransit usa duck typing por interface quando necessário.

### Exemplo de contrato

```csharp
// Shared/Contracts/Events/OrderPlaced.cs
namespace Shared.Contracts.Events;

public record OrderPlaced(
    Guid CorrelationId,   // = OrderId
    string CustomerId,
    decimal TotalAmount,
    IReadOnlyList<OrderItem> Items,
    DateTime PlacedAt
);

public record OrderItem(
    string ProductId,
    int Quantity,
    decimal UnitPrice
);
```

## Organização no Shared

```
Shared/
  Contracts/
    Events/
      OrderPlaced.cs
      PaymentCompleted.cs
      PaymentFailed.cs
      PaymentCancelled.cs
      InventoryReserved.cs
      InventoryFailed.cs
      InventoryCancelled.cs
      ShippingScheduled.cs
      ShippingFailed.cs
    Commands/
      ProcessPayment.cs
      ReserveInventory.cs
      ScheduleShipping.cs
      CancelPayment.cs
      CancelInventory.cs
```

## Decisões Técnicas

- **`CorrelationId` em todos os contratos:** permite que a state machine correlacione automaticamente
- **`record` imutável:** facilita serialização/desserialização JSON (MassTransit usa System.Text.Json por padrão)
- **Sem `ICommand` / `IEvent` interfaces:** MassTransit não exige — interfaces são opcionais e adicionam complexidade sem benefício no PoC
- **Namespace `Shared.Contracts.Events` e `Shared.Contracts.Commands`:** separação semântica clara para fins didáticos

## Critérios de Aceite

1. Todos os 14 contratos definidos como `record` no `Shared`
2. Todos os contratos compilam com `dotnet build src/Shared/`
3. Contratos existentes (`*Command`, `*Reply`) mantidos temporariamente para compatibilidade (remoção via `mt-cleanup`)
4. `CorrelationId` presente em todos os contratos
