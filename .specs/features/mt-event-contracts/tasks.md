# Tasks: mt-event-contracts

**Feature:** mt-event-contracts
**Milestone:** M10 - Migração MassTransit

## Resumo

3 tarefas independentes entre si (podem ser executadas em qualquer ordem). Sem dependências externas — apenas o `Shared.csproj` precisa ter o pacote MassTransit.

---

## T1 — Criar contratos de eventos dos serviços de domínio

**Diretório:** `src/Shared/Contracts/Events/`

**O que fazer:**

Criar os seguintes arquivos como `record`:

1. `OrderPlaced.cs` — `CorrelationId`, `CustomerId`, `TotalAmount`, `Items`, `PlacedAt`
2. `PaymentCompleted.cs` — `CorrelationId`, `PaymentId`, `ProcessedAt`
3. `PaymentFailed.cs` — `CorrelationId`, `Reason`
4. `PaymentCancelled.cs` — `CorrelationId`, `PaymentId`
5. `InventoryReserved.cs` — `CorrelationId`, `ReservationId`, `ReservedAt`
6. `InventoryFailed.cs` — `CorrelationId`, `Reason`
7. `InventoryCancelled.cs` — `CorrelationId`, `ReservationId`
8. `ShippingScheduled.cs` — `CorrelationId`, `TrackingCode`, `ScheduledAt`
9. `ShippingFailed.cs` — `CorrelationId`, `Reason`

Também criar `OrderItem.cs` no mesmo diretório (usado por `OrderPlaced`):
- `ProductId`, `Quantity`, `UnitPrice`

**Verificação:** `dotnet build src/Shared/` sem erros.

---

## T2 — Criar contratos de comandos (saga → serviços)

**Diretório:** `src/Shared/Contracts/Commands/`

**O que fazer:**

Criar os seguintes arquivos como `record`:

1. `ProcessPayment.cs` — `CorrelationId`, `Amount`, `CustomerId`
2. `ReserveInventory.cs` — `CorrelationId`, `Items` (lista de `OrderItem`)
3. `ScheduleShipping.cs` — `CorrelationId`, `Items`, `ShippingAddress`
4. `CancelPayment.cs` — `CorrelationId`, `PaymentId`
5. `CancelInventory.cs` — `CorrelationId`, `ReservationId`

Também criar `ShippingAddress.cs`:
- `Street`, `City`, `PostalCode`, `Country`

**Verificação:** `dotnet build src/Shared/` sem erros.

---

## T3 — Adicionar pacote MassTransit ao Shared.csproj

**Arquivo:** `src/Shared/Shared.csproj`

**O que fazer:**

Adicionar referência ao pacote MassTransit (versão compatível com .NET 10):
```xml
<PackageReference Include="MassTransit" Version="8.*" />
```

> Nota: verificar versão mais recente compatível com .NET 10 antes de fixar a versão.

**Verificação:** `dotnet restore src/Shared/` e `dotnet build src/Shared/` sem erros.

---

## Dependências

```
T3 → T1, T2 (Shared precisa do pacote)
T1 e T2 são independentes entre si
```

Ordem recomendada: T3 → T1 → T2 (ou T3 → T1 e T2 em paralelo)
