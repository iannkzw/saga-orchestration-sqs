# Tasks: mt-concurrency

**Feature:** mt-concurrency
**Milestone:** M10 - Migração MassTransit

## Resumo

3 tarefas. Depende de `mt-state-machine` (entidade `OrderSagaInstance` deve existir) e `mt-db-migration` (schema configurado).

---

## T1 — Configurar UseXminAsConcurrencyToken no OrderSagaInstance

**Arquivo:** `src/OrderService/Data/OrderDbContext.cs`

**O que fazer:**

Em `OnModelCreating`, adicionar:
```csharp
modelBuilder.Entity<OrderSagaInstance>()
    .UseXminAsConcurrencyToken();
```

Verificar que o pacote `Npgsql.EntityFrameworkCore.PostgreSQL` está presente (deve estar via `mt-db-migration`).

**Verificação:** `dotnet ef migrations add AddXminConcurrency` gera migration sem coluna extra (xmin é coluna de sistema do PostgreSQL).

---

## T2 — Configurar ConcurrencyMode.Optimistic no repositório MassTransit

**Arquivo:** `src/OrderService/Program.cs`

**O que fazer:**

Na configuração `AddSagaStateMachine`, adicionar:
```csharp
.EntityFrameworkRepository(r =>
{
    r.ConcurrencyMode = ConcurrencyMode.Optimistic;
    r.AddDbContext<DbContext, OrderDbContext>((provider, options) =>
        options.UseNpgsql(configuration.GetConnectionString("DefaultConnection")));
});
```

**Verificação:** `OrderService` inicia com log indicando modo otimista.

---

## T3 — Configurar retry para conflitos de concorrência

**Arquivo:** `src/OrderService/Program.cs`

**O que fazer:**

Na configuração `UsingAmazonSqs`, adicionar retry para toda a fila da saga:
```csharp
sqsCfg.UseMessageRetry(r =>
    r.Intervals(TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromMilliseconds(500)));
```

Adicionar log de retry:
```csharp
sqsCfg.UseInMemoryOutbox();  // garante atomicidade durante retry
```

**Verificação:** Sob concorrência forçada, log mostra `[Retry] Tentativa X de 3 — DbUpdateConcurrencyException`.

---

## Dependências

```
mt-state-machine (OrderSagaInstance existe) → T1
mt-db-migration (schema do EF configurado) → T1
T1 → T2 (repositório EF precisa da configuração de concorrência)
T2 → T3 (retry precisa do repositório configurado)
mt-program-config coordena T2 e T3
```
