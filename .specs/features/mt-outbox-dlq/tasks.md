# Tasks: mt-outbox-dlq

**Feature:** mt-outbox-dlq
**Milestone:** M10 - Migração MassTransit

## Resumo

4 tarefas. Depende de `mt-db-migration` (migrations EF Core) e `mt-messaging-infra` (MassTransit configurado com SQS).

---

## T1 — Configurar AddEntityFrameworkOutbox no OrderService

**Arquivo:** `src/OrderService/Program.cs`

**O que fazer:**

Dentro de `AddMassTransit`, adicionar configuração do Outbox conforme spec.md:
```csharp
cfg.AddEntityFrameworkOutbox<OrderDbContext>(o =>
{
    o.UsePostgres();
    o.UseBusOutbox();
    o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
    o.QueryDelay = TimeSpan.FromSeconds(1);
    o.QueryTimeout = TimeSpan.FromSeconds(30);
    o.LockStatementProvider = new PostgresLockStatementProvider();
});
```

**Verificação:** `OrderService` inicia sem erros. Log mostra outbox background worker iniciado.

---

## T2 — Criar migration EF Core para tabelas do Outbox

**O que fazer:**

Executar:
```bash
dotnet ef migrations add AddMassTransitOutbox --project src/OrderService/
```

Verificar que a migration gerada contém as tabelas:
- `outbox_message`
- `outbox_state`
- `inbox_state`

**Verificação:** Migration gerada sem erros. `dotnet ef database update` aplica sem erros.

---

## T3 — Configurar retry e DLQ por endpoint

**Arquivo:** `src/OrderService/Program.cs`

**O que fazer:**

Na configuração `UsingAmazonSqs`, para o endpoint da saga:
```csharp
sqsCfg.ReceiveEndpoint("order-saga", ep =>
{
    ep.UseMessageRetry(r => r.Intervals(
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5)));
    ep.UseEntityFrameworkOutbox<OrderDbContext>(context);
    ep.ConfigureSaga<OrderSagaInstance>(context);
});
```

**Verificação:** Mensagem intencionalmente falha vai para `order-saga_error` após 4 tentativas.

---

## T4 — Atualizar endpoints GET /dlq e POST /dlq/redrive

**Arquivo:** `src/OrderService/Api/DlqEndpoints.cs` (ou onde estão os endpoints DLQ atuais)

**O que fazer:**

1. Atualizar `GET /dlq` para listar mensagens das filas `*_error` gerenciadas pelo MassTransit (não mais as filas DLQ manuais antigas)
2. Atualizar `POST /dlq/redrive` para mover mensagem de `order-saga_error` de volta para `order-saga`
3. Manter a API idêntica — apenas a fila fonte muda

**Verificação:** `GET /dlq` retorna mensagens presentes em `order-saga_error` no LocalStack.

---

## Dependências

```
mt-db-migration (OrderDbContext configurado) → T1, T2
mt-messaging-infra (UsingAmazonSqs configurado) → T1, T3
T1, T2 → T3
T3 → T4
```
