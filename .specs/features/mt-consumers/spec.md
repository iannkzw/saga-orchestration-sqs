# Feature: mt-consumers

**Milestone:** M10 - Migração MassTransit
**Status:** PLANNED

## Objetivo

Implementar os consumidores MassTransit em cada serviço de domínio (`PaymentService`, `InventoryService`, `ShippingService`), substituindo os Workers de polling SQS manual. Cada consumidor recebe um comando via MassTransit, executa a lógica de negócio e publica um evento de resultado.

## Consumidores por Serviço

### PaymentService
| Consumidor | Consome | Publica |
|-----------|---------|---------|
| `ProcessPaymentConsumer` | `ProcessPayment` | `PaymentCompleted` ou `PaymentFailed` |
| `CancelPaymentConsumer` | `CancelPayment` | `PaymentCancelled` |

### InventoryService
| Consumidor | Consome | Publica |
|-----------|---------|---------|
| `ReserveInventoryConsumer` | `ReserveInventory` | `InventoryReserved` ou `InventoryFailed` |
| `CancelInventoryConsumer` | `CancelInventory` | `InventoryCancelled` |

### ShippingService
| Consumidor | Consome | Publica |
|-----------|---------|---------|
| `ScheduleShippingConsumer` | `ScheduleShipping` | `ShippingScheduled` ou `ShippingFailed` |

## Padrão de Implementação

Cada consumidor segue o mesmo padrão:

```csharp
public class ProcessPaymentConsumer : IConsumer<ProcessPayment>
{
    private readonly IPaymentProcessor _processor;
    private readonly ILogger<ProcessPaymentConsumer> _logger;

    public async Task Consume(ConsumeContext<ProcessPayment> context)
    {
        var msg = context.Message;
        _logger.LogInformation("[Payment] Processando pagamento para pedido {OrderId}", msg.CorrelationId);

        var result = await _processor.ProcessAsync(msg.CorrelationId, msg.Amount, context.CancellationToken);

        if (result.Success)
            await context.Publish(new PaymentCompleted(msg.CorrelationId, result.PaymentId, DateTime.UtcNow));
        else
            await context.Publish(new PaymentFailed(msg.CorrelationId, result.ErrorMessage));
    }
}
```

## Lógica de Negócio Preservada

Os consumidores devem reaproveitarr a lógica existente nos Workers manuais:
- `PaymentService`: aprovação/rejeição de pagamento (simulado)
- `InventoryService`: `TryReserveAsync` ou `TryReserveOptimisticAsync` (conforme `INVENTORY_LOCKING_MODE`)
- `ShippingService`: agendamento de entrega (simulado)

As classes de repositório/processamento existentes são mantidas — apenas o transporte muda.

## Organização

```
src/
  PaymentService/
    Consumers/
      ProcessPaymentConsumer.cs
      CancelPaymentConsumer.cs
  InventoryService/
    Consumers/
      ReserveInventoryConsumer.cs
      CancelInventoryConsumer.cs
  ShippingService/
    Consumers/
      ScheduleShippingConsumer.cs
```

## Decisões Técnicas

- **`context.Publish` (não `context.Send`):** eventos são publicados para o exchange MassTransit, não enviados diretamente para uma fila. A state machine no `OrderService` recebe via subscription
- **Sem `IRequestClient`:** o padrão command/reply é substituído por publish/subscribe assíncrono
- **Worker.cs removido de cada serviço:** os consumidores MassTransit substituem completamente os `BackgroundService` de polling

## Critérios de Aceite

1. `ProcessPaymentConsumer` recebe `ProcessPayment`, publica `PaymentCompleted` ou `PaymentFailed`
2. `CancelPaymentConsumer` recebe `CancelPayment`, publica `PaymentCancelled`
3. `ReserveInventoryConsumer` recebe `ReserveInventory`, publica `InventoryReserved` ou `InventoryFailed`; respeita `INVENTORY_LOCKING_MODE`
4. `CancelInventoryConsumer` recebe `CancelInventory`, publica `InventoryCancelled`
5. `ScheduleShippingConsumer` recebe `ScheduleShipping`, publica `ShippingScheduled` ou `ShippingFailed`
6. Workers manuais (`Worker.cs`) removidos dos 3 serviços
