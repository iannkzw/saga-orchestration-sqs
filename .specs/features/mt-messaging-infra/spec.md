# Feature: mt-messaging-infra

**Milestone:** M10 - Migração MassTransit
**Status:** DONE

## Objetivo

Configurar o transporte MassTransit com Amazon SQS (via LocalStack) em todos os serviços. O MassTransit deve criar automaticamente as filas SQS necessárias ao iniciar, substituindo o script `init-sqs.sh` e a criação manual de filas via AWS CLI.

## Configuração UsingAmazonSqs

O MassTransit suporta SQS nativamente via pacote `MassTransit.AmazonSQS`. A configuração usa `UsingAmazonSqs` no lugar de `UsingInMemory` ou outros transportes.

```csharp
services.AddMassTransit(cfg =>
{
    // consumidores/state machine registrados aqui

    cfg.UsingAmazonSqs((context, sqsCfg) =>
    {
        sqsCfg.Host("us-east-1", h =>
        {
            h.Config(new AmazonSQSConfig { ServiceURL = "http://localstack:4566" });
            h.Config(new AmazonSimpleNotificationServiceConfig { ServiceURL = "http://localstack:4566" });
            h.Credentials(new BasicAWSCredentials("test", "test"));
        });

        sqsCfg.ConfigureEndpoints(context);
    });
});
```

## Filas Criadas Automaticamente

O MassTransit cria filas baseadas nos nomes dos endpoints dos consumidores:

| Fila SQS | Serviço | Consumidor |
|----------|---------|-----------|
| `process-payment` | PaymentService | `ProcessPaymentConsumer` |
| `cancel-payment` | PaymentService | `CancelPaymentConsumer` |
| `reserve-inventory` | InventoryService | `ReserveInventoryConsumer` |
| `cancel-inventory` | InventoryService | `CancelInventoryConsumer` |
| `schedule-shipping` | ShippingService | `ScheduleShippingConsumer` |
| `order-saga` | OrderService | `OrderStateMachine` |

Filas de eventos (SNS Topics + SQS subscriptions) também são criadas automaticamente pelo MassTransit.

## Filas Removidas

Com o MassTransit, as seguintes filas manuais deixam de existir:
- `payment-commands`, `payment-replies`
- `inventory-commands`, `inventory-replies`
- `shipping-commands`, `shipping-replies`
- `order-status-updates`
- `*-dlq` (MassTransit cria DLQs automaticamente com sufixo `_error`)

## Componente Compartilhado

Criar extension method em `Shared/Extensions/MassTransitExtensions.cs` com a configuração de host SQS, para evitar duplicação nos 4 serviços:

```csharp
public static void ConfigureSqsHost(this IAmazonSqsBusFactoryConfigurator cfg, IConfiguration configuration)
{
    var serviceUrl = configuration["AWS_SQS_SERVICE_URL"] ?? "http://localstack:4566";
    cfg.Host("us-east-1", h =>
    {
        h.Config(new AmazonSQSConfig { ServiceURL = serviceUrl });
        h.Config(new AmazonSimpleNotificationServiceConfig { ServiceURL = serviceUrl });
        h.Credentials(new BasicAWSCredentials("test", "test"));
    });
}
```

## Decisões Técnicas

- **`ConfigureEndpoints(context)`:** deixa o MassTransit descobrir e configurar endpoints automaticamente baseado nos consumidores registrados
- **Topologia SNS+SQS:** MassTransit usa SNS para fan-out de eventos (publish) e SQS para recebimento. Cada serviço tem suas filas SQS inscritas nos topics SNS relevantes
- **DLQ automática:** MassTransit cria `{endpoint}_error` como DLQ após N tentativas (configurável via `UseMessageRetry`)
- **`AWS_SQS_SERVICE_URL` configurável:** permite apontar para LocalStack em dev e para SQS real em outros ambientes

## Critérios de Aceite

1. `docker compose up` sobe os 4 serviços e todas as filas SQS são criadas no LocalStack sem script manual
2. Log de startup de cada serviço mostra filas configuradas pelo MassTransit
3. `init-sqs.sh` pode ser removido ou deixado como fallback (a ser decidido em `mt-infra-docker`)
4. Filas de erro (`*_error`) criadas automaticamente
