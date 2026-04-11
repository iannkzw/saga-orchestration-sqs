# Feature: mt-idempotency

**Milestone:** M10 - Migração MassTransit
**Status:** PLANNED

## Objetivo

Substituir o `IdempotencyStore` manual (tabela `idempotency_keys` + verificação explícita em cada handler) pela idempotência nativa do MassTransit: correlação da saga via `CorrelationId` e `DuplicateDetectionWindow` no Outbox.

## Mecanismo Atual (a remover)

O sistema atual usa:
1. `IdempotencyStore` — tabela PostgreSQL com chaves processadas
2. Verificação explícita em cada Worker: `if (await _idempotency.AlreadyProcessed(messageId)) return;`
3. Tabela `idempotency_keys` com `message_id` e `processed_at`

Problemas:
- Duplicação de lógica em todos os Workers
- Tabela adicional a gerenciar
- Não funciona bem com processamento paralelo (race condition na verificação)

## Mecanismo Novo (MassTransit)

### 1. Correlação da Saga

O MassTransit garante que para um dado `CorrelationId`, apenas uma instância de saga existe. Se um evento duplicado chegar, a state machine recebe o evento no estado atual e pode ignorá-lo via `Ignore()` ou tratar idempotentemente via `DuringAny`:

```csharp
DuringAny(
    When(PaymentCompleted)
        .If(ctx => ctx.Saga.CurrentState == nameof(PaymentProcessing),
            then => then.TransitionTo(InventoryReserving).PublishAsync(...))
        // Se não está em PaymentProcessing, evento é ignorado silenciosamente
);
```

### 2. Outbox com DuplicateDetectionWindow

O MassTransit Outbox (EF Core) usa `DuplicateDetectionWindow` para evitar reprocessamento de mensagens dentro de uma janela de tempo:

```csharp
cfg.UseEntityFrameworkOutbox<OrderDbContext>(o =>
{
    o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
    o.QueryDelay = TimeSpan.FromSeconds(1);
    o.QueryTimeout = TimeSpan.FromSeconds(30);
});
```

### 3. Remoção do IdempotencyStore

- Remover tabela `idempotency_keys`
- Remover classe `IdempotencyStore` e interface `IIdempotencyStore`
- Remover registros de DI e usages em Workers
- Remover de `Shared.csproj` se lá estiver

## O que NÃO é coberto pelo MassTransit

Os consumidores nos serviços de domínio (`PaymentService`, etc.) precisam ser idempotentes na camada de negócio:
- `ProcessPaymentConsumer`: se o mesmo `CorrelationId` chegar duas vezes, verificar se pagamento já existe antes de processar
- `ReserveInventoryConsumer`: o `reservationId` já é gerado com `NewId.NextGuid()` — se a mesma `CorrelationId` chegar novamente, verificar `inventory_reservations` antes de reservar

Essa idempotência de negócio é tratada dentro de cada consumidor, sem necessidade de store separado.

## Critérios de Aceite

1. Tabela `idempotency_keys` não existe mais
2. Classe `IdempotencyStore` e interface removidas do codebase
3. `DuplicateDetectionWindow = 30min` configurado no Outbox do `OrderService`
4. Reenvio de evento duplicado para a saga não causa dupla transição de estado
5. Consumidores de domínio verificam idempotência internamente antes de processar
