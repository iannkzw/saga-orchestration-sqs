# 08 — Exemplo Completo

← [07 — Transactional Outbox & DLQ](./07-transactional-outbox-and-dlq.md) | [Voltar ao índice](./00-overview.md)

---

Este arquivo apresenta um exemplo coeso e executável da migração completa. Cada trecho de código
indica qual parte do projeto atual ele substitui.

---

## Contratos de Eventos (substitui os Replies manuais)

Os eventos tipados substituem as filas de reply dedicadas. Sem `SagaId` em cada reply — o
`CorrelationId` é o próprio `SagaId`.

```csharp
// src/Shared/Contracts/Events/PaymentEvents.cs
// Substitui: src/Shared/Contracts/Replies/PaymentReply.cs e RefundPaymentReply.cs
namespace Shared.Contracts.Events;

public record PaymentCompleted
{
    public Guid SagaId { get; init; }
    public string TransactionId { get; init; } = string.Empty;
}

public record PaymentFailed
{
    public Guid SagaId { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
}

public record PaymentRefunded
{
    public Guid SagaId { get; init; }
    public string RefundId { get; init; } = string.Empty;
}
```

```csharp
// src/Shared/Contracts/Events/InventoryEvents.cs
// Substitui: src/Shared/Contracts/Replies/InventoryReply.cs e ReleaseInventoryReply.cs
namespace Shared.Contracts.Events;

public record InventoryReserved
{
    public Guid SagaId { get; init; }
    public string ReservationId { get; init; } = string.Empty;
}

public record InventoryFailed
{
    public Guid SagaId { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
}

public record InventoryReleased
{
    public Guid SagaId { get; init; }
}
```

```csharp
// src/Shared/Contracts/Events/ShippingEvents.cs
// Substitui: src/Shared/Contracts/Replies/ShippingReply.cs e CancelShippingReply.cs
namespace Shared.Contracts.Events;

public record ShippingScheduled
{
    public Guid SagaId { get; init; }
    public string TrackingNumber { get; init; } = string.Empty;
}

public record ShippingFailed
{
    public Guid SagaId { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
}

public record ShippingCancelled
{
    public Guid SagaId { get; init; }
}
```

```csharp
// src/Shared/Contracts/Events/SagaTerminated.cs
// Substitui: src/Shared/Contracts/Notifications/SagaTerminatedNotification.cs
namespace Shared.Contracts.Events;

public record SagaTerminated
{
    public Guid SagaId { get; init; }
    public Guid OrderId { get; init; }
    public string TerminalState { get; init; } = string.Empty;

    public SagaTerminated() { }
    public SagaTerminated(Guid sagaId, Guid orderId, string terminalState)
    {
        SagaId        = sagaId;
        OrderId       = orderId;
        TerminalState = terminalState;
    }
}
```

---

## `OrderSagaState.cs` — Estado da saga

```csharp
// src/SagaOrchestrator/Saga/OrderSagaState.cs
//
// Substitui:
//   - src/SagaOrchestrator/Models/SagaInstance.cs       (entidade + TransitionTo)
//   - src/SagaOrchestrator/Models/SagaState.cs          (enum)
//   - src/SagaOrchestrator/Models/SagaStateTransition.cs (log de transições)
//
// O que muda:
//   - CompensationDataJson (dicionário JSON) → propriedades tipadas
//   - RowVersion adicionado (resolve dívida técnica de concorrência)
//   - CurrentState é string gerenciada pelo MassTransit
using MassTransit;

namespace SagaOrchestrator.Saga;

public class OrderSagaState : SagaStateMachineInstance
{
    // ── Obrigatório ───────────────────────────────────────────────────────────
    // Correlaciona todas as mensagens desta saga; equivale a SagaInstance.Id
    public Guid CorrelationId { get; set; }

    // Estado atual — gerenciado pelo MassTransit, persistido como string
    public string CurrentState { get; set; } = string.Empty;

    // ── Dados do pedido ───────────────────────────────────────────────────────
    public Guid OrderId { get; set; }
    public decimal TotalAmount { get; set; }
    public string ItemsJson { get; set; } = "[]";
    public string? SimulateFailure { get; set; }

    // ── Dados de compensação (tipados) ────────────────────────────────────────
    // Substitui CompensationDataJson = "{}" com Dictionary<string,string>
    public string? PaymentTransactionId { get; set; }
    public string? InventoryReservationId { get; set; }
    public string? ShippingTrackingNumber { get; set; }

    // ── Controle de concorrência ──────────────────────────────────────────────
    // Resolve a dívida técnica: TODO de concorrência em Worker.cs
    public byte[] RowVersion { get; set; } = [];

    // ── Auditoria ─────────────────────────────────────────────────────────────
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

---

## `OrderStateMachine.cs` — Máquina de estados completa

```csharp
// src/SagaOrchestrator/Saga/OrderStateMachine.cs
//
// Substitui:
//   - src/SagaOrchestrator/StateMachine/SagaStateMachine.cs (classe estática com 3 dicionários)
//   - src/SagaOrchestrator/Worker.cs:
//       HandleSuccessAsync, HandleFailureAsync, HandleCompensationReplyAsync
//       SendForwardCommandAsync, SendCompensationCommandAsync
//       StoreCompensationData, GetCompensationData
//
// Redução estimada: ~250 linhas
using MassTransit;
using Shared.Contracts.Commands;
using Shared.Contracts.Events;

namespace SagaOrchestrator.Saga;

public class OrderStateMachine : MassTransitStateMachine<OrderSagaState>
{
    // ── Estados ───────────────────────────────────────────────────────────────
    public State PaymentProcessing  { get; private set; } = null!;
    public State InventoryReserving { get; private set; } = null!;
    public State ShippingScheduling { get; private set; } = null!;

    // Estados de compensação
    public State ShippingCancelling { get; private set; } = null!;
    public State InventoryReleasing { get; private set; } = null!;
    public State PaymentRefunding   { get; private set; } = null!;

    // ── Eventos ───────────────────────────────────────────────────────────────
    public Event<CreateOrder>       OrderCreated      { get; private set; } = null!;
    public Event<PaymentCompleted>  PaymentCompleted  { get; private set; } = null!;
    public Event<PaymentFailed>     PaymentFailed     { get; private set; } = null!;
    public Event<InventoryReserved> InventoryReserved { get; private set; } = null!;
    public Event<InventoryFailed>   InventoryFailed   { get; private set; } = null!;
    public Event<ShippingScheduled> ShippingScheduled { get; private set; } = null!;
    public Event<ShippingFailed>    ShippingFailed    { get; private set; } = null!;
    public Event<ShippingCancelled> ShippingCancelled { get; private set; } = null!;
    public Event<InventoryReleased> InventoryReleased { get; private set; } = null!;
    public Event<PaymentRefunded>   PaymentRefunded   { get; private set; } = null!;

    public OrderStateMachine()
    {
        // Propriedade que armazena o estado no banco
        InstanceState(x => x.CurrentState);

        // ── Correlações ───────────────────────────────────────────────────────
        // Substitui: extração manual de SagaId via JsonElement + FirstOrDefaultAsync
        Event(() => OrderCreated,
            x => x.CorrelateById(ctx => ctx.Message.OrderId));
        Event(() => PaymentCompleted,
            x => x.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => PaymentFailed,
            x => x.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => InventoryReserved,
            x => x.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => InventoryFailed,
            x => x.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => ShippingScheduled,
            x => x.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => ShippingFailed,
            x => x.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => ShippingCancelled,
            x => x.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => InventoryReleased,
            x => x.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => PaymentRefunded,
            x => x.CorrelateById(ctx => ctx.Message.SagaId));

        // ── Happy Path ────────────────────────────────────────────────────────
        // Substitui: POST /sagas em Program.cs + início do fluxo em Worker.cs

        Initially(
            When(OrderCreated)
                .Then(ctx =>
                {
                    ctx.Saga.OrderId       = ctx.Message.OrderId;
                    ctx.Saga.TotalAmount   = ctx.Message.TotalAmount;
                    ctx.Saga.ItemsJson     = ctx.Message.ItemsJson;
                    ctx.Saga.SimulateFailure = ctx.Message.SimulateFailure;
                    ctx.Saga.CreatedAt     = DateTime.UtcNow;
                    ctx.Saga.UpdatedAt     = DateTime.UtcNow;
                })
                .Send(ctx => new ProcessPayment
                {
                    SagaId    = ctx.Saga.CorrelationId,
                    OrderId   = ctx.Saga.OrderId,
                    Amount    = ctx.Saga.TotalAmount,
                    Timestamp = DateTime.UtcNow
                })
                .TransitionTo(PaymentProcessing)
        );

        During(PaymentProcessing,
            When(PaymentCompleted)
                .Then(ctx =>
                {
                    // Substitui: StoreCompensationData / data["TransactionId"]
                    ctx.Saga.PaymentTransactionId = ctx.Message.TransactionId;
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .Send(ctx => new ReserveInventory
                {
                    SagaId    = ctx.Saga.CorrelationId,
                    OrderId   = ctx.Saga.OrderId,
                    Items     = DeserializeItems(ctx.Saga.ItemsJson),
                    Timestamp = DateTime.UtcNow
                })
                .TransitionTo(InventoryReserving),

            When(PaymentFailed)
                // Pagamento falhou → nada a compensar → Failed
                .Then(ctx => ctx.Saga.UpdatedAt = DateTime.UtcNow)
                .Publish(ctx => new SagaTerminated(ctx.Saga.CorrelationId, ctx.Saga.OrderId, "Failed"))
                .Finalize()
        );

        During(InventoryReserving,
            When(InventoryReserved)
                .Then(ctx =>
                {
                    // Substitui: data["ReservationId"]
                    ctx.Saga.InventoryReservationId = ctx.Message.ReservationId;
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .Send(ctx => new ScheduleShipping
                {
                    SagaId          = ctx.Saga.CorrelationId,
                    OrderId         = ctx.Saga.OrderId,
                    ShippingAddress = "Endereço padrão (PoC)",
                    Timestamp       = DateTime.UtcNow
                })
                .TransitionTo(ShippingScheduling),

            When(InventoryFailed)
                // Estoque falhou → compensar pagamento
                .Then(ctx => ctx.Saga.UpdatedAt = DateTime.UtcNow)
                .Send(ctx => new RefundPayment
                {
                    SagaId        = ctx.Saga.CorrelationId,
                    OrderId       = ctx.Saga.OrderId,
                    Amount        = ctx.Saga.TotalAmount,
                    // Substitui: compData.GetValueOrDefault("TransactionId", "")
                    TransactionId = ctx.Saga.PaymentTransactionId!,
                    Timestamp     = DateTime.UtcNow
                })
                .TransitionTo(PaymentRefunding)
        );

        During(ShippingScheduling,
            When(ShippingScheduled)
                .Then(ctx =>
                {
                    // Substitui: data["TrackingNumber"]
                    ctx.Saga.ShippingTrackingNumber = ctx.Message.TrackingNumber;
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                // Saga concluída com sucesso
                .Publish(ctx => new SagaTerminated(ctx.Saga.CorrelationId, ctx.Saga.OrderId, "Completed"))
                .Finalize(),

            When(ShippingFailed)
                // Frete falhou → compensar estoque primeiro
                .Then(ctx => ctx.Saga.UpdatedAt = DateTime.UtcNow)
                .Send(ctx => new ReleaseInventory
                {
                    SagaId        = ctx.Saga.CorrelationId,
                    OrderId       = ctx.Saga.OrderId,
                    ReservationId = ctx.Saga.InventoryReservationId!,
                    Timestamp     = DateTime.UtcNow
                })
                .TransitionTo(InventoryReleasing)
        );

        // ── Cadeia de Compensação ─────────────────────────────────────────────
        // Substitui: HandleCompensationReplyAsync + _compensationTransitions dict
        // + SendCompensationCommandAsync switch

        During(InventoryReleasing,
            When(InventoryReleased)
                .Then(ctx => ctx.Saga.UpdatedAt = DateTime.UtcNow)
                .Send(ctx => new RefundPayment
                {
                    SagaId        = ctx.Saga.CorrelationId,
                    OrderId       = ctx.Saga.OrderId,
                    Amount        = ctx.Saga.TotalAmount,
                    TransactionId = ctx.Saga.PaymentTransactionId!,
                    Timestamp     = DateTime.UtcNow
                })
                .TransitionTo(PaymentRefunding)
        );

        During(PaymentRefunding,
            When(PaymentRefunded)
                .Then(ctx => ctx.Saga.UpdatedAt = DateTime.UtcNow)
                // Compensação completa
                .Publish(ctx => new SagaTerminated(ctx.Saga.CorrelationId, ctx.Saga.OrderId, "Failed"))
                .Finalize()
        );

        // Remover instâncias finalizadas do repositório (optional — mantém BD limpo)
        SetCompletedWhenFinalized();
    }

    private static List<InventoryItem> DeserializeItems(string itemsJson)
    {
        try
        {
            var orderItems = System.Text.Json.JsonSerializer.Deserialize<List<OrderItem>>(itemsJson) ?? [];
            return orderItems.Select(i => new InventoryItem
            {
                ProductId = i.ProductId,
                Quantity  = i.Quantity
            }).ToList();
        }
        catch
        {
            return [];
        }
    }
}
```

---

## `ProcessPaymentConsumer.cs` — Consumer do PaymentService

```csharp
// src/PaymentService/Consumers/ProcessPaymentConsumer.cs
//
// Substitui:
//   - src/PaymentService/Worker.cs:
//       HandleProcessPaymentAsync (sem idempotência manual, sem polling, sem DeleteMessageAsync)
//   - As filas "payment-replies" e "payment-replies-dlq" são eliminadas
//
// O que muda:
//   - IConsumer<T> em vez de BackgroundService
//   - Publish<T> em vez de SendMessageAsync para fila de reply
//   - Sem IdempotencyStore — MassTransit gerencia re-entrega
using MassTransit;
using Shared.Contracts.Commands;
using Shared.Contracts.Events;

namespace PaymentService.Consumers;

public class ProcessPaymentConsumer : IConsumer<ProcessPayment>
{
    private readonly ILogger<ProcessPaymentConsumer> _logger;

    public ProcessPaymentConsumer(ILogger<ProcessPaymentConsumer> logger)
    {
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ProcessPayment> context)
    {
        var command = context.Message;

        _logger.LogInformation(
            "Processando pagamento: SagaId={SagaId}, OrderId={OrderId}, Amount={Amount}",
            command.SagaId, command.OrderId, command.Amount);

        // Verificar flag de simulação de falha (equivalente ao MessageAttribute "SimulateFailure")
        var shouldFail = command.SimulateFailure?.Equals("payment", StringComparison.OrdinalIgnoreCase) == true;

        await Task.Delay(200, context.CancellationToken);  // simular latência

        if (shouldFail)
        {
            // Publicar evento de falha — MassTransit roteia para a saga automaticamente
            // Sem SendMessageAsync, sem URL de fila, sem MessageAttributes manuais
            await context.Publish(new PaymentFailed
            {
                SagaId       = command.SagaId,
                ErrorMessage = "Falha simulada no pagamento"
            });
        }
        else
        {
            var transactionId = Guid.NewGuid().ToString();

            _logger.LogInformation(
                "Pagamento aprovado: SagaId={SagaId}, TransactionId={TransactionId}",
                command.SagaId, transactionId);

            await context.Publish(new PaymentCompleted
            {
                SagaId        = command.SagaId,
                TransactionId = transactionId
            });
        }

        // MassTransit chama DeleteMessageAsync automaticamente após Consume() retornar sem exceção
    }
}
```

---

## `Program.cs` do SagaOrchestrator — Registro completo no DI

```csharp
// src/SagaOrchestrator/Program.cs
//
// Substitui:
//   - src/SagaOrchestrator/Program.cs (completo — 257 linhas → ~80 linhas)
//   - src/Shared/Extensions/ServiceCollectionExtensions.cs (AddSagaConnectivity)
//   - src/Shared/Configuration/SqsConfig.cs (nomes de filas manuais)
//   - src/SagaOrchestrator/Worker.cs (toda a lógica de polling e mensageria)
//   - Endpoints /dlq e /dlq/redrive são eliminados
//
// Redução estimada do arquivo: ~180 linhas
using Amazon.SQS;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using SagaOrchestrator.Data;
using SagaOrchestrator.Saga;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("SagaDb")
    ?? "Host=localhost;Port=5432;Database=saga_db;Username=saga;Password=saga_pass";

// ── Entity Framework ──────────────────────────────────────────────────────────
builder.Services.AddDbContext<SagaDbContext>(options =>
    options.UseNpgsql(connectionString));

// ── MassTransit ───────────────────────────────────────────────────────────────
builder.Services.AddMassTransit(cfg =>
{
    // Saga com persistência EF Core + concorrência otimista
    cfg.AddSagaStateMachine<OrderStateMachine, OrderSagaState>()
       .EntityFrameworkRepository(r =>
       {
           r.ConcurrencyMode = ConcurrencyMode.Optimistic;  // resolve dívida técnica
           r.AddDbContext<DbContext, SagaDbContext>((provider, options) =>
               options.UseNpgsql(connectionString));
       });

    // Outbox transacional — resolve dual-write (TODO no Worker.cs original)
    cfg.AddEntityFrameworkOutbox<SagaDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
        o.DuplicateDetectionWindow = TimeSpan.FromHours(1);
        o.QueryDelay = TimeSpan.FromSeconds(1);
    });

    // Transporte SQS — substitui toda a infraestrutura manual de filas
    cfg.UsingAmazonSqs((context, config) =>
    {
        var sqsServiceUrl = builder.Configuration["AWS_SERVICE_URL"] ?? "http://localhost:4566";

        config.Host("us-east-1", h =>
        {
            h.AccessKey("test");
            h.SecretKey("test");
            h.Config(new AmazonSQSConfig { ServiceURL = sqsServiceUrl });
        });

        // Retry antes de mover para fila de erro
        config.UseMessageRetry(r =>
        {
            r.Intervals(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(15));
        });

        // Criar e configurar todos os endpoints automaticamente
        // Substitui: SqsConfig.cs + GetQueueUrlAsync manual + init-sqs.sh
        config.ConfigureEndpoints(context);
    });
});

// ── OpenTelemetry ─────────────────────────────────────────────────────────────
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            .AddSource("MassTransit")  // substitui SqsTracePropagation manual
            .AddSource("Npgsql")
            .AddAspNetCoreInstrumentation()
            .AddOtlpExporter();
    });

var app = builder.Build();

// Criar tabelas (saga state + outbox tables)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SagaDbContext>();
    await db.Database.MigrateAsync();  // usa migrations em vez de EnsureCreated
}

// ── Endpoints HTTP ────────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "SagaOrchestrator" }));

// Iniciar saga via endpoint HTTP — publica CreateOrder no bus
app.MapPost("/sagas", async (CreateOrderRequest request, IBus bus) =>
{
    var sagaId = Guid.NewGuid();

    await bus.Publish(new CreateOrder
    {
        OrderId        = request.OrderId,
        TotalAmount    = request.TotalAmount,
        ItemsJson      = System.Text.Json.JsonSerializer.Serialize(request.Items),
        SimulateFailure = request.SimulateFailure
    });

    return Results.Accepted($"/sagas/{sagaId}", new { sagaId });
});

// Consultar estado da saga via EF
app.MapGet("/sagas/{id:guid}", async (Guid id, SagaDbContext db) =>
{
    var saga = await db.OrderSagas.FindAsync(id);
    if (saga is null)
        return Results.NotFound();

    return Results.Ok(new
    {
        sagaId       = saga.CorrelationId,
        orderId      = saga.OrderId,
        currentState = saga.CurrentState,
        createdAt    = saga.CreatedAt,
        updatedAt    = saga.UpdatedAt
    });
});

// Endpoints /dlq e /dlq/redrive REMOVIDOS — gerenciados pelo MassTransit automaticamente

app.Run();
```

---

## `SagaDbContext.cs` — Contexto EF Core com tabelas do Outbox

```csharp
// src/SagaOrchestrator/Data/SagaDbContext.cs
//
// Substitui:
//   - src/SagaOrchestrator/Data/SagaDbContext.cs (versão atual)
//   - Tabela "idempotency_keys" criada manualmente via IdempotencyStore.EnsureTableAsync
//   - Tabelas do Outbox são gerenciadas pelo MassTransit EF migrations
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using SagaOrchestrator.Saga;

namespace SagaOrchestrator.Data;

public class SagaDbContext : DbContext
{
    public SagaDbContext(DbContextOptions<SagaDbContext> options) : base(options) { }

    // Saga state
    public DbSet<OrderSagaState> OrderSagas => Set<OrderSagaState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<OrderSagaState>(entity =>
        {
            entity.ToTable("order_sagas");
            entity.HasKey(e => e.CorrelationId);

            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id");
            entity.Property(e => e.CurrentState).HasColumnName("current_state").HasMaxLength(64);
            entity.Property(e => e.OrderId).HasColumnName("order_id");
            entity.Property(e => e.TotalAmount).HasColumnName("total_amount");
            entity.Property(e => e.ItemsJson).HasColumnName("items_json");
            entity.Property(e => e.SimulateFailure).HasColumnName("simulate_failure");
            entity.Property(e => e.PaymentTransactionId).HasColumnName("payment_transaction_id");
            entity.Property(e => e.InventoryReservationId).HasColumnName("inventory_reservation_id");
            entity.Property(e => e.ShippingTrackingNumber).HasColumnName("shipping_tracking_number");

            // Controle de concorrência otimista — resolve dívida técnica
            entity.Property(e => e.RowVersion)
                  .HasColumnName("row_version")
                  .IsRowVersion();

            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        });

        // Tabelas do Outbox — adicionadas ao modelo pelo MassTransit
        // Substituem: tabela "idempotency_keys" criada manualmente
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
```

---

## Resumo de o que cada arquivo substitui

| Arquivo MassTransit (novo) | Substitui (atual) |
|---|---|
| `OrderSagaState.cs` | `SagaInstance.cs` + `SagaState.cs` (enum) + `SagaStateTransition.cs` |
| `OrderStateMachine.cs` | `SagaStateMachine.cs` (estática) + `HandleSuccessAsync` + `HandleFailureAsync` + `HandleCompensationReplyAsync` + `StoreCompensationData` + `SendForwardCommandAsync` + `SendCompensationCommandAsync` |
| `ProcessPaymentConsumer.cs` (e demais) | `PaymentService/Worker.cs` (loop + handlers + idempotência manual) |
| `Program.cs` (MassTransit) | `Program.cs` (atual 257 linhas) + `SqsConfig.cs` + `ServiceCollectionExtensions.cs` + endpoints DLQ |
| `SagaDbContext.cs` (MassTransit) | `SagaDbContext.cs` (atual) + `IdempotencyStore.EnsureTableAsync` (DDL manual) |
| Eventos tipados (`PaymentCompleted`, etc.) | Replies (`PaymentReply`, `InventoryReply`, `ShippingReply`, etc.) + filas reply SQS |

---

## Checklist de migração

- [ ] Adicionar pacotes NuGet:
  - `MassTransit` (core)
  - `MassTransit.AmazonSQS` (transporte SQS)
  - `MassTransit.EntityFrameworkCore` (saga repository + Outbox)
- [ ] Criar contratos de eventos em `src/Shared/Contracts/Events/`
- [ ] Criar `OrderSagaState.cs` com propriedades tipadas e `RowVersion`
- [ ] Criar `OrderStateMachine.cs` com happy path + cadeia de compensação declarativa
- [ ] Criar consumers `IConsumer<T>` para cada comando em cada serviço
- [ ] Atualizar `SagaDbContext` para incluir tabelas do Outbox
- [ ] Criar migration EF para as novas tabelas
- [ ] Atualizar `Program.cs` do SagaOrchestrator com `AddMassTransit`
- [ ] Atualizar `Program.cs` de cada serviço com `AddMassTransit` + consumers
- [ ] Remover: `SqsConfig.cs`, `IdempotencyStore.cs`, `SqsTracePropagation.cs`, `Worker.cs` (todos)
- [ ] Atualizar `docker-compose.yml` — remover filas de reply do `init-sqs.sh`
- [ ] Executar testes de integração para validar o comportamento equivalente

> Voltar ao [índice geral →](./00-overview.md)
