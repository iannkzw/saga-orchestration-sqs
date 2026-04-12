# Feature: mt-outbox-dlq

**Milestone:** M10 - Migração MassTransit
**Status:** IMPLEMENTING

## Objetivo

Configurar o EF Core Outbox do MassTransit no `OrderService` para resolver o problema de dual-write, e garantir que mensagens não processáveis vão para a DLQ (`*_error`) após N tentativas. O Outbox garante atomicidade entre a transição de estado da saga e a publicação de mensagens SQS.

## Problema Atual (Dual-Write)

O fluxo manual atual tem um problema de dual-write:
1. Salvar estado da saga no PostgreSQL
2. Enviar mensagem SQS

Se o processo falhar entre os passos 1 e 2, o estado é salvo mas a mensagem não é enviada. O sistema fica inconsistente.

## Solução: MassTransit EF Core Outbox

O Outbox persiste as mensagens a serem publicadas **na mesma transação** que a atualização do estado da saga. Um processo em background (`IHostedService`) depois envia as mensagens para o SQS:

```
Transação DB:
  1. UPDATE order_saga_instances SET current_state = 'InventoryReserving'
  2. INSERT INTO outbox_messages (message_body, destination_address, ...)
  COMMIT

Background worker:
  3. SELECT FROM outbox_messages WHERE sent_at IS NULL
  4. SQS.SendMessage(...)
  5. UPDATE outbox_messages SET sent_at = NOW()
```

Se o processo cair entre 3 e 5, na próxima inicialização o background worker reenviará a mensagem (at-least-once delivery + DuplicateDetectionWindow para deduplicação).

## Configuração

```csharp
// OrderService/Program.cs
services.AddMassTransit(cfg =>
{
    cfg.AddEntityFrameworkOutbox<OrderDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
        o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
        o.QueryDelay = TimeSpan.FromSeconds(1);
        o.QueryTimeout = TimeSpan.FromSeconds(30);
        o.LockStatementProvider = new PostgresLockStatementProvider();
    });
    // ...
});
```

## Tabelas do Outbox

O MassTransit cria automaticamente (via migration EF Core):

| Tabela | Descrição |
|--------|-----------|
| `outbox_message` | Mensagens a enviar |
| `outbox_state` | Estado do processador do outbox |
| `inbox_state` | Deduplicação de mensagens recebidas |

## DLQ (Dead Letter Queue)

O MassTransit cria automaticamente `{endpoint}_error` como DLQ. Após esgotar as tentativas de retry (`UseMessageRetry`), a mensagem vai para a DLQ.

Configuração de retry e DLQ:
```csharp
sqsCfg.ReceiveEndpoint("order-saga", ep =>
{
    ep.UseMessageRetry(r => r.Intervals(500, 1000, 2000, 5000));
    ep.UseEntityFrameworkOutbox<OrderDbContext>(context);
    ep.ConfigureSaga<OrderSagaInstance>(context);
});
```

A DLQ `order-saga_error` no SQS recebe mensagens após 4 tentativas falhas.

## Endpoint de Inspeção de DLQ

Manter endpoint `GET /dlq` e `POST /dlq/redrive` no `OrderService` (existente na feature `dlq-visibility`), apontando agora para as filas `*_error` gerenciadas pelo MassTransit.

## Critérios de Aceite

1. Tabelas `outbox_message`, `outbox_state`, `inbox_state` criadas via EF migration
2. Publicação de eventos e atualização de estado da saga são atômicas (mesma transação)
3. Simulação de falha entre DB commit e SQS send é recuperável na próxima inicialização
4. Após 4 tentativas falhas, mensagem vai para `order-saga_error` no SQS
5. `GET /dlq` lista mensagens em `*_error`; `POST /dlq/redrive` move mensagem de volta para a fila principal
