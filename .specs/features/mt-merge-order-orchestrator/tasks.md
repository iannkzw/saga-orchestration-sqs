# Tasks: mt-merge-order-orchestrator

**Feature:** mt-merge-order-orchestrator
**Milestone:** M10 - Migração MassTransit

## Resumo

5 tarefas de reorganização estrutural. Devem ser executadas em ordem — as tarefas de código dependem da estrutura de pastas.

---

## T1 — Criar estrutura de pastas no OrderService

**O que fazer:**

Criar os diretórios dentro de `src/OrderService/`:
- `Api/` — mover endpoints Minimal API
- `Consumers/` — futuros consumidores MassTransit
- `StateMachine/` — futura state machine
- `Data/` — DbContext unificado
- `Models/` — entidades

**Verificação:** `ls src/OrderService/` mostra as pastas criadas.

---

## T2 — Remover chamada HTTP do OrderService para SagaOrchestrator

**Arquivo:** `src/OrderService/`

**O que fazer:**

1. Localizar onde `OrderService` faz `HttpClient.PostAsync` para o orquestrador
2. Substituir essa chamada por publicação de evento MassTransit (`IPublishEndpoint.Publish<OrderPlaced>(...)`)
3. Remover `HttpClient` registrado para SagaOrchestrator no `Program.cs`
4. Remover variável de ambiente `SAGA_ORCHESTRATOR_URL` do `OrderService`

**Verificação:** `OrderService` compila sem referências a `HttpClient` para orquestrador.

---

## T3 — Mover lógica de criação de pedido para camada Api/

**Arquivo:** `src/OrderService/Program.cs` → `src/OrderService/Api/OrderEndpoints.cs`

**O que fazer:**

1. Extrair handler do `POST /orders` e `GET /orders/{id}` para classe `OrderEndpoints`
2. Registrar via `app.MapGroup("/orders").MapOrderEndpoints()` no `Program.cs`
3. Manter comportamento exato — apenas mover código, não alterar lógica

**Verificação:** `POST /orders` e `GET /orders/{id}` continuam funcionando.

---

## T4 — Unificar OrderDbContext (pedidos + saga)

**Arquivo:** `src/OrderService/Data/OrderDbContext.cs`

**O que fazer:**

1. Criar `OrderDbContext` consolidado com:
   - `DbSet<Order> Orders`
   - `DbSet<OrderSagaInstance> SagaInstances` (será usado pela feature `mt-state-machine`)
2. Remover referência ao `SagaDbContext` do `SagaOrchestrator`
3. Registrar no DI via `Program.cs`

**Verificação:** `OrderDbContext` compila com os dois DbSets.

---

## T5 — Remover dependência de projeto SagaOrchestrator do OrderService

**Arquivo:** `src/OrderService/OrderService.csproj`

**O que fazer:**

1. Remover `<ProjectReference>` para `SagaOrchestrator` se existir
2. Remover qualquer `using SagaOrchestrator.*` nos arquivos do OrderService
3. Verificar que `dotnet build src/OrderService/` compila sem o projeto orquestrador

**Verificação:** `dotnet build src/OrderService/OrderService.csproj` compila sem erros.

---

## Dependências

```
T1 → T2, T3, T4 (pastas precisam existir)
T2, T3, T4 → T5 (remoção de deps)
```

Ordem: T1 → T2 → T3 → T4 → T5
