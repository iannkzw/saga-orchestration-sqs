# Tasks: mt-consumers

**Feature:** mt-consumers
**Milestone:** M10 - Migração MassTransit

## Resumo

5 tarefas, uma por consumidor (ou par de consumidores por serviço). Depende de `mt-event-contracts` (contratos devem existir). T1, T2 e T3 são independentes entre si.

---

## T1 — Consumidores do PaymentService

**Diretório:** `src/PaymentService/Consumers/`

**O que fazer:**

1. Criar `ProcessPaymentConsumer : IConsumer<ProcessPayment>`
   - Chama lógica de pagamento existente
   - Publica `PaymentCompleted` (sucesso) ou `PaymentFailed` (falha)
   - Log estruturado com `CorrelationId`

2. Criar `CancelPaymentConsumer : IConsumer<CancelPayment>`
   - Estorna/cancela o pagamento (simulado)
   - Publica `PaymentCancelled`

3. Remover `Worker.cs` do PaymentService (polling manual substituído)

**Verificação:** `dotnet build src/PaymentService/` sem erros.

---

## T2 — Consumidores do InventoryService

**Diretório:** `src/InventoryService/Consumers/`

**O que fazer:**

1. Criar `ReserveInventoryConsumer : IConsumer<ReserveInventory>`
   - Lê `INVENTORY_LOCKING_MODE` via `IConfiguration`
   - Despacha para `TryReserveAsync` (pessimistic/none) ou `TryReserveOptimisticAsync` (optimistic)
   - Publica `InventoryReserved` (sucesso) ou `InventoryFailed` (falha)

2. Criar `CancelInventoryConsumer : IConsumer<CancelInventory>`
   - Chama `ReleaseAsync` no repositório existente
   - Publica `InventoryCancelled`

3. Remover `Worker.cs` do InventoryService

**Verificação:** `dotnet build src/InventoryService/` sem erros. Modo de locking respeitado.

---

## T3 — Consumidor do ShippingService

**Diretório:** `src/ShippingService/Consumers/`

**O que fazer:**

1. Criar `ScheduleShippingConsumer : IConsumer<ScheduleShipping>`
   - Chama lógica de agendamento existente
   - Publica `ShippingScheduled` (sucesso) ou `ShippingFailed` (falha)

2. Remover `Worker.cs` do ShippingService

**Verificação:** `dotnet build src/ShippingService/` sem erros.

---

## T4 — Criar definições de consumidor (ConsumerDefinition)

**O que fazer:**

Para cada consumidor, criar `*ConsumerDefinition` herdando `ConsumerDefinition<T>`:
- Configurar `EndpointName` correspondente à fila (ex: `process-payment`, `reserve-inventory`)
- Configurar `ConcurrentMessageLimit` conservador (default: 4)

Isso garante que o MassTransit crie as filas corretas no SQS.

**Verificação:** Filas criadas no LocalStack com os nomes corretos ao iniciar os serviços.

---

## T5 — Registrar consumidores via AddConsumer no MassTransit

> Nota: este task é executado em conjunto com `mt-program-config` para cada serviço.

**O que fazer:**

Em cada `Program.cs`, dentro da configuração MassTransit, adicionar:
```csharp
cfg.AddConsumer<ProcessPaymentConsumer, ProcessPaymentConsumerDefinition>();
cfg.AddConsumer<CancelPaymentConsumer, CancelPaymentConsumerDefinition>();
// etc.
```

**Verificação:** Consumidores aparecem no log de startup do MassTransit como "registered".

---

## Dependências

```
mt-event-contracts (contratos devem existir) → T1, T2, T3
T1, T2, T3 → T4 (definições dependem dos consumidores)
T4 → T5 (registro no Program.cs)
mt-program-config executa T5 de forma coordenada
```
