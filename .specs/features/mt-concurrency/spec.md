# Feature: mt-concurrency

**Milestone:** M10 - Migração MassTransit
**Status:** PLANNED

## Objetivo

Adicionar controle de concorrência otimista via `RowVersion` na entidade `OrderSagaInstance`, configurar o MassTransit para usar o modo `Optimistic` no repositório de saga EF Core, e garantir que múltiplas mensagens simultâneas para a mesma saga não causem inconsistência.

## Contexto

O MassTransit EF Core saga repository suporta dois modos de concorrência:

| Modo | Comportamento |
|------|-------------|
| `Pessimistic` | `SELECT FOR UPDATE` — serializa acesso, garante consistência, menor throughput |
| `Optimistic` | Usa `RowVersion` / `xmin` (PostgreSQL) — detecta conflito via `DbUpdateConcurrencyException`, faz retry automático |

O modo `Optimistic` é mais alinhado com o padrão de sagas (cada mensagem processa sequencialmente para uma dada correlação) e tem melhor throughput para sagas de serviços diferentes.

## Implementação

### 1. `RowVersion` na `OrderSagaInstance`

```csharp
public class OrderSagaInstance : SagaStateMachineInstance
{
    // ... outras propriedades
    public byte[] RowVersion { get; set; }  // para SQL Server
    // OU
    public uint xmin { get; set; }  // para PostgreSQL (coluna de sistema)
}
```

**Para PostgreSQL com Npgsql:** usar o token de concorrência nativo via `UseXminAsConcurrencyToken()`:

```csharp
// No OnModelCreating:
modelBuilder.Entity<OrderSagaInstance>()
    .UseXminAsConcurrencyToken();
```

### 2. Configuração do repositório MassTransit

```csharp
cfg.AddSagaStateMachine<OrderStateMachine, OrderSagaInstance>()
    .EntityFrameworkRepository(r =>
    {
        r.ConcurrencyMode = ConcurrencyMode.Optimistic;
        r.AddDbContext<DbContext, OrderDbContext>(...);
    });
```

### 3. Retry automático em conflito

O MassTransit com modo `Optimistic` lança `DbUpdateConcurrencyException` internamente e faz retry da mensagem. Configurar:

```csharp
cfg.UseMessageRetry(r => r.Intervals(100, 200, 500));  // 3 retries com backoff
```

## Decisões Técnicas

- **`UseXminAsConcurrencyToken`:** solução nativa do PostgreSQL/Npgsql — não adiciona coluna extra, usa coluna de sistema `xmin` que é atualizada em todo UPDATE
- **Modo `Optimistic` preferido sobre `Pessimistic`:** sagas bem-desenhadas raramente têm conflito real (cada saga tem seu próprio `CorrelationId`)
- **Retry via MassTransit, não EF Core:** o MassTransit reprocessa a mensagem; o EF Core apenas detecta o conflito

## Critérios de Aceite

1. `OrderSagaInstance` tem `UseXminAsConcurrencyToken()` configurado no EF Core
2. MassTransit configurado com `ConcurrencyMode.Optimistic`
3. Retry de 3 tentativas configurado para `DbUpdateConcurrencyException`
4. Sob 5 mensagens simultâneas para a mesma saga, todas são processadas sem inconsistência (retry resolve conflitos)
5. Log mostra retry quando conflito de concorrência ocorre
