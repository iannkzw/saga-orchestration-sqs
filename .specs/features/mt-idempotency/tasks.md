# Tasks: mt-idempotency

**Feature:** mt-idempotency
**Milestone:** M10 - Migração MassTransit

## Resumo

4 tarefas. Depende de `mt-outbox-dlq` (Outbox precisa estar configurado para `DuplicateDetectionWindow`). As tarefas de remoção (T1, T2) são independentes das de configuração (T3, T4).

---

## T1 — Remover IdempotencyStore e tabela idempotency_keys

**O que fazer:**

1. Localizar e remover `IIdempotencyStore.cs`, `IdempotencyStore.cs` (ou similar)
2. Remover migration ou DDL que cria `idempotency_keys`
3. Remover registro de DI em todos os `Program.cs`
4. Remover todos os usages de `IIdempotencyStore` nos Workers (verificações `AlreadyProcessed`)

**Verificação:** `dotnet build` sem erros após remoção. `grep -r "IdempotencyStore\|idempotency_keys" src/` sem resultados.

---

## T2 — Adicionar idempotência de negócio nos consumidores

**Arquivos:** `src/PaymentService/Consumers/`, `src/InventoryService/Consumers/`

**O que fazer:**

1. `ProcessPaymentConsumer`: antes de processar, verificar se já existe pagamento para o `CorrelationId`. Se sim, publicar `PaymentCompleted` com o `PaymentId` existente (replay idempotente)
2. `ReserveInventoryConsumer`: verificar `inventory_reservations` para o `CorrelationId`. Se reserva já existe, publicar `InventoryReserved` com o `ReservationId` existente
3. `ScheduleShippingConsumer`: verificar se envio já foi agendado para o `CorrelationId`. Se sim, publicar `ShippingScheduled` com dados existentes

**Verificação:** Reenviar mesmo comando duas vezes não cria duplicatas no banco nem causa inconsistência.

---

## T3 — Configurar DuplicateDetectionWindow no Outbox

**Arquivo:** `src/OrderService/Program.cs`

**O que fazer:**

Na configuração do MassTransit Outbox (dentro de `mt-outbox-dlq`), adicionar:
```csharp
o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
```

**Verificação:** Configuração presente no `Program.cs` do `OrderService`.

---

## T4 — Configurar DuringAny para ignorar eventos duplicados na state machine

**Arquivo:** `src/OrderService/StateMachine/OrderStateMachine.cs`

**O que fazer:**

Adicionar `DuringAny` para cada evento de resposta, verificando se o estado atual é compatível. Se não for, o evento é ignorado silenciosamente (MassTransit acknowledges sem processar):

```csharp
DuringAny(
    When(PaymentCompleted)
        .If(ctx => ctx.Saga.CurrentState != nameof(PaymentProcessing),
            then => then.Then(ctx =>
                _logger.LogWarning("PaymentCompleted ignorado — estado atual: {State}", ctx.Saga.CurrentState)))
);
```

**Verificação:** Evento duplicado logado como warning, sem transição de estado.

---

## Dependências

```
mt-outbox-dlq (Outbox configurado) → T3
mt-consumers (consumidores existem) → T2
T1 pode ser executado antes de qualquer outra tarefa
T4 depende de mt-state-machine (state machine existe)
```
