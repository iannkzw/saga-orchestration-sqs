# Feature: mt-program-config

**Milestone:** M10 - Migração MassTransit
**Status:** PLANNED

## Objetivo

Configurar o MassTransit em cada um dos 4 `Program.cs` dos serviços, integrando os consumidores, a state machine, o transporte SQS e o Outbox de forma coesa. Esta feature é a "cola" que une todas as outras features de implementação.

## Configuração por Serviço

### OrderService

```csharp
builder.Services.AddMassTransit(cfg =>
{
    // State machine
    cfg.AddSagaStateMachine<OrderStateMachine, OrderSagaInstance, OrderStateMachineDefinition>()
       .EntityFrameworkRepository(r =>
       {
           r.ConcurrencyMode = ConcurrencyMode.Optimistic;
           r.AddDbContext<DbContext, OrderDbContext>(...);
       });

    // Outbox
    cfg.AddEntityFrameworkOutbox<OrderDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
        o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
    });

    // Transporte
    cfg.UsingAmazonSqs((context, sqsCfg) =>
    {
        sqsCfg.ConfigureSqsHost(configuration);
        sqsCfg.UseMessageRetry(r => r.Intervals(500, 1000, 2000, 5000));
        sqsCfg.ConfigureEndpoints(context);
    });
});
```

### PaymentService

```csharp
builder.Services.AddMassTransit(cfg =>
{
    cfg.AddConsumer<ProcessPaymentConsumer, ProcessPaymentConsumerDefinition>();
    cfg.AddConsumer<CancelPaymentConsumer, CancelPaymentConsumerDefinition>();

    cfg.UsingAmazonSqs((context, sqsCfg) =>
    {
        sqsCfg.ConfigureSqsHost(configuration);
        sqsCfg.UseMessageRetry(r => r.Intervals(500, 1000, 2000));
        sqsCfg.ConfigureEndpoints(context);
    });
});
```

### InventoryService

```csharp
builder.Services.AddMassTransit(cfg =>
{
    cfg.AddConsumer<ReserveInventoryConsumer, ReserveInventoryConsumerDefinition>();
    cfg.AddConsumer<CancelInventoryConsumer, CancelInventoryConsumerDefinition>();

    cfg.UsingAmazonSqs((context, sqsCfg) =>
    {
        sqsCfg.ConfigureSqsHost(configuration);
        sqsCfg.UseMessageRetry(r => r.Intervals(500, 1000, 2000));
        sqsCfg.ConfigureEndpoints(context);
    });
});
```

### ShippingService

```csharp
builder.Services.AddMassTransit(cfg =>
{
    cfg.AddConsumer<ScheduleShippingConsumer, ScheduleShippingConsumerDefinition>();

    cfg.UsingAmazonSqs((context, sqsCfg) =>
    {
        sqsCfg.ConfigureSqsHost(configuration);
        sqsCfg.UseMessageRetry(r => r.Intervals(500, 1000, 2000));
        sqsCfg.ConfigureEndpoints(context);
    });
});
```

## Variáveis de Ambiente por Serviço

Todas lidas via `IConfiguration`:

| Variável | Serviço | Padrão | Descrição |
|----------|---------|--------|-----------|
| `AWS_SQS_SERVICE_URL` | Todos | `http://localstack:4566` | URL do SQS |
| `AWS_REGION` | Todos | `us-east-1` | Região AWS |
| `ConnectionStrings__DefaultConnection` | OrderService | — | PostgreSQL |
| `INVENTORY_LOCKING_MODE` | InventoryService | `pessimistic` | Modo de locking |
| `INVENTORY_OPTIMISTIC_MAX_RETRIES` | InventoryService | `3` | Retries otimistas |

## Decisões Técnicas

- **`ConfigureEndpoints(context)`:** autodiscovery de endpoints baseado nos consumidores registrados via DI
- **`UseMessageRetry` global:** aplica retry a todos os endpoints do serviço; retries específicos podem ser configurados por endpoint nos `ConsumerDefinition`
- **`Worker.cs` como `IHostedService` removido:** o MassTransit registra automaticamente seus background services via `AddMassTransit`

## Critérios de Aceite

1. Todos os 4 `Program.cs` têm `AddMassTransit` configurado
2. `docker compose up` inicia todos os serviços sem erro
3. Log de startup de cada serviço mostra consumidores/state machine registrados
4. Primeiro pedido criado via `POST /orders` dispara o fluxo completo da saga
