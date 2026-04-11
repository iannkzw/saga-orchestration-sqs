# Tasks: mt-db-migration

**Feature:** mt-db-migration
**Milestone:** M10 - Migração MassTransit

## Resumo

5 tarefas. Depende de `mt-state-machine` (entidade `OrderSagaInstance`). Precede `mt-outbox-dlq` e `mt-concurrency`.

---

## T1 — Consolidar OrderDbContext com Order + OrderSagaInstance

**Arquivo:** `src/OrderService/Data/OrderDbContext.cs`

**O que fazer:**

1. Garantir que `OrderDbContext` tem `DbSet<Order> Orders` e `DbSet<OrderSagaInstance> SagaInstances`
2. Configurar mapeamento em `OnModelCreating`:
   - `orders`: snake_case, PK `id`
   - `order_saga_instances`: snake_case, PK `correlation_id`, índice em `order_id`
3. Remover referência ao `SagaDbContext` do projeto `SagaOrchestrator`

**Verificação:** `dotnet build src/OrderService/` compila com ambas as entidades mapeadas.

---

## T2 — Criar migration InitialCreate

**O que fazer:**

```bash
cd src/OrderService
dotnet ef migrations add InitialCreate --output-dir Data/Migrations
```

Verificar que a migration gerada contém:
- `CreateTable orders`
- `CreateTable order_saga_instances`

Revisar e ajustar se necessário (nomes de colunas snake_case, tipos corretos).

**Verificação:** Migration gerada sem erros. SQL gerado está correto.

---

## T3 — Substituir EnsureCreated por MigrateAsync no startup

**Arquivo:** `src/OrderService/Program.cs`

**O que fazer:**

Substituir:
```csharp
await dbContext.Database.EnsureCreatedAsync();
```
Por:
```csharp
await dbContext.Database.MigrateAsync();
```

Adicionar retry de startup para aguardar o PostgreSQL estar disponível:
```csharp
var retryPolicy = Policy.Handle<Exception>()
    .WaitAndRetryAsync(5, i => TimeSpan.FromSeconds(i * 2));
await retryPolicy.ExecuteAsync(() => dbContext.Database.MigrateAsync());
```

**Verificação:** `docker compose up` aplica a migration e `\dt` no psql mostra as tabelas.

---

## T4 — Remover tabelas antigas (migration de cleanup)

**O que fazer:**

Criar migration de cleanup para remover tabelas antigas se existirem:
```bash
dotnet ef migrations add RemoveLegacyTables --output-dir Data/Migrations
```

A migration deve usar `migrationBuilder.Sql` para dropar:
```sql
DROP TABLE IF EXISTS sagas;
DROP TABLE IF EXISTS saga_state_transitions;
DROP TABLE IF EXISTS idempotency_keys;
```

**Verificação:** `docker compose up` a partir de um banco limpo não tem as tabelas antigas.

---

## T5 — Remover SagaDbContext do projeto SagaOrchestrator

**O que fazer:**

1. Localizar `SagaOrchestrator/Data/SagaDbContext.cs` e remover o arquivo
2. Remover migrations do `SagaOrchestrator` se existirem
3. Remover registro de DI do `SagaDbContext` no `SagaOrchestrator/Program.cs`
4. Garantir que `dotnet build src/SagaOrchestrator/` ainda compila (se o projeto ainda existir antes do `mt-cleanup`)

**Verificação:** Nenhuma referência a `SagaDbContext` no codebase.

---

## Dependências

```
mt-state-machine (OrderSagaInstance existe) → T1, T2
T1 → T2 (entidades configuradas antes de gerar migration)
T2 → T3 (migration criada antes de aplicar)
T3 → T4 (banco migrado antes de cleanup)
mt-cleanup (remove SagaOrchestrator) coordena T5
```
