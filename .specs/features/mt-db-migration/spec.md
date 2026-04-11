# Feature: mt-db-migration

**Milestone:** M10 - Migração MassTransit
**Status:** PLANNED

## Objetivo

Unificar os contextos EF Core em um único `OrderDbContext` no `OrderService`, criar as migrations formais para o schema novo (substituindo `EnsureCreated`/`EnsureTablesAsync`), e garantir que o banco de dados seja inicializado corretamente ao subir o contêiner.

## Contextos EF Core Atuais

| Projeto | Contexto | Tabelas |
|---------|----------|---------|
| `OrderService` | `OrderDbContext` (se existir) | `orders` |
| `SagaOrchestrator` | `SagaDbContext` | `sagas`, `saga_state_transitions` |
| `InventoryService` | Acesso direto via Npgsql | `inventory`, `inventory_reservations` |
| `PaymentService` | Acesso direto via Npgsql | `payments` |

## Contexto Unificado Alvo

### OrderService (PostgreSQL — banco `saga_db`)

`OrderDbContext` unificado com:
- `DbSet<Order> Orders` → tabela `orders`
- `DbSet<OrderSagaInstance> SagaInstances` → tabela `order_saga_instances`
- Tabelas do Outbox MassTransit: `outbox_message`, `outbox_state`, `inbox_state` (via migration MassTransit)

### Serviços de Domínio (sem mudança em EF Core)

`PaymentService`, `InventoryService` e `ShippingService` continuam usando Npgsql direto (sem EF Core) — `EnsureTablesAsync` com DDL explícito. Não há necessidade de EF Core nesses serviços para o PoC.

## Schema Alvo

### Tabela `orders` (mantida)

| Coluna | Tipo | Descrição |
|--------|------|-----------|
| `id` | UUID (PK) | ID do pedido |
| `customer_id` | VARCHAR | ID do cliente |
| `status` | VARCHAR(50) | Status atual (Pending, Processing, Completed, Failed) |
| `total_amount` | DECIMAL | Valor total |
| `created_at` | TIMESTAMP | Data de criação |
| `updated_at` | TIMESTAMP | Última atualização |

### Tabela `order_saga_instances` (nova)

| Coluna | Tipo | Descrição |
|--------|------|-----------|
| `correlation_id` | UUID (PK) | = OrderId |
| `current_state` | VARCHAR(100) | Estado MassTransit |
| `order_id` | UUID | Referência ao pedido |
| `customer_id` | VARCHAR | ID do cliente |
| `total_amount` | DECIMAL | Valor total |
| `payment_id` | VARCHAR | ID do pagamento (nullable) |
| `reservation_id` | VARCHAR | ID da reserva (nullable) |
| `compensation_step` | VARCHAR(50) | Passo de compensação atual (nullable) |
| `failure_reason` | TEXT | Motivo da falha (nullable) |
| `created_at` | TIMESTAMP | Data de criação |
| `updated_at` | TIMESTAMP | Última atualização |

### Tabelas Removidas

- `sagas` (substituída por `order_saga_instances`)
- `saga_state_transitions` (histórico removido — state machine declarativa é auto-documentada)
- `idempotency_keys` (substituída pelo Outbox MassTransit)

## Estratégia de Migration

- **Substituir `EnsureCreated` por `MigrateAsync`** no startup do `OrderService`
- **Primeira migration:** `InitialCreate` com `orders` e `order_saga_instances`
- **Segunda migration:** adicionada pelo MassTransit Outbox (automaticamente)
- **Serviços de domínio:** mantêm `EnsureTablesAsync` via Npgsql direto

## Critérios de Aceite

1. `dotnet ef migrations add InitialCreate` gera migration com `orders` e `order_saga_instances`
2. `OrderService` chama `MigrateAsync()` no startup em vez de `EnsureCreated()`
3. `docker compose up` cria todas as tabelas sem script SQL manual
4. Tabelas antigas (`sagas`, `saga_state_transitions`, `idempotency_keys`) não existem mais
5. `dotnet build` sem erros após remoção do `SagaDbContext`
