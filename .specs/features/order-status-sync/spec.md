# Feature: order-status-sync

**Milestone:** M9 - Sincronização de Status do Pedido
**Status:** TODO

## Objetivo

Corrigir o bug em que `Order.Status` fica permanentemente preso em `Processing`. O `SagaOrchestrator` já conhece os estados terminais (`Completed` / `Failed`) ao processar replies SQS, mas não notifica o `OrderService`. Esta feature introduz um canal de notificação assíncrono via SQS para que o `OrderService` atualize `Order.Status` quando a saga terminar.

## Contexto do Problema

1. `POST /orders` cria o pedido com `Status = Pending`, chama `POST /sagas` no orquestrador via HTTP e seta `Status = Processing` — esse valor nunca mais é alterado.
2. O `SagaOrchestrator.Worker` detecta estados terminais em `HandleSuccessAsync` (→ `Completed`) e `HandleCompensationReplyAsync` (→ `Failed`), mas apenas persiste no próprio banco e nada notifica o `OrderService`.
3. `GET /orders/{id}` consulta o orquestrador via HTTP e exibe o estado da saga no response, mas o campo `Order.Status` no banco do `OrderService` permanece `Processing`.

## Requisitos Funcionais

1. Quando o `SagaOrchestrator` atingir estado terminal (`Completed` ou `Failed`), deve publicar uma notificação na fila `order-status-updates` com `OrderId`, `SagaId` e o estado terminal.
2. O `OrderService` deve ter um Worker que consome a fila `order-status-updates` e atualiza `Order.Status` no PostgreSQL.
3. O mapeamento de estado terminal da saga para `OrderStatus` deve ser:
   - `SagaState.Completed` → `OrderStatus.Completed`
   - `SagaState.Failed` → `OrderStatus.Failed`
4. Se o `OrderId` da notificação não existir no banco do `OrderService`, o Worker deve logar e descartar a mensagem (não relançar erro).
5. O Worker deve deletar a mensagem da fila após atualização bem-sucedida.
6. A fila `order-status-updates` deve ter DLQ configurada (mesma política das demais: `maxReceiveCount=3`).

## Requisitos Não-Funcionais

1. O Worker deve seguir o mesmo padrão dos Workers existentes (`BackgroundService`, long-poll com `WaitTimeSeconds`, loop com `CancellationToken`).
2. A notificação deve usar o contrato compartilhado em `Shared/Contracts/` para evitar acoplamento via strings.
3. A publicação da notificação deve ocorrer **dentro do mesmo bloco de processamento** que persiste o estado terminal no banco do `SagaOrchestrator`, evitando inconsistência entre o estado da saga e a notificação (best-effort, sem Outbox neste PoC).
4. A fila deve seguir a convenção de nomenclatura: `order-status-updates`.

## Componentes Afetados

| Componente | Mudança |
|-----------|---------|
| `Shared/Configuration/SqsConfig.cs` | Adicionar constante `OrderStatusUpdates = "order-status-updates"` |
| `Shared/Contracts/Notifications/` | Criar `SagaTerminatedNotification.cs` |
| `SagaOrchestrator/Worker.cs` | Publicar notificação em `HandleSuccessAsync` e `HandleCompensationReplyAsync` ao atingir estado terminal |
| `OrderService/Worker.cs` | Novo `BackgroundService` que consome `order-status-updates` e atualiza `Order.Status` |
| `OrderService/Program.cs` | Registrar `Worker` como `HostedService` |
| `infra/localstack/init-queues.sh` | Criar fila `order-status-updates` e sua DLQ |
| `README.md` | Atualizar demo do happy path para incluir `GET /orders/{orderId}` mostrando `status: "Completed"`; cenários de falha mostrando `status: "Failed"` |
| `docs/08-guia-pratico.md` | Atualizar Cenário 1 (Passo 2) com resposta esperada de order status `Completed`; atualizar Cenário 2 com status `Failed` |
| `tests/IntegrationTests/Infrastructure/SagaClient.cs` | Adicionar `GetOrderAsync(orderId)` que consulta `GET /orders/{orderId}` e retorna `OrderResponse` |
| `tests/IntegrationTests/Models/OrderResponse.cs` | Novo DTO para resposta de `GET /orders/{id}` com campos `OrderId`, `SagaId`, `Status` |
| `tests/IntegrationTests/Tests/HappyPathTests.cs` | Adicionar assertion: após saga `Completed`, `GET /orders/{orderId}` retorna `status = "Completed"` |
| `tests/IntegrationTests/Tests/CompensationTests.cs` | Adicionar assertion: após saga `Failed`, `GET /orders/{orderId}` retorna `status = "Failed"` |
| `scripts/happy-path-demo.sh` | Adicionar verificação de `GET /orders/{orderId}` após poll da saga; assertar status correto em cada cenário |

## Criterios de Aceite

1. Após um fluxo happy path completo, `GET /orders/{id}` retorna `order.status = "Completed"` no campo do próprio banco (não apenas no campo da saga embarcada).
2. Após um fluxo com falha/compensação, `GET /orders/{id}` retorna `order.status = "Failed"`.
3. A fila `order-status-updates` é criada no LocalStack no `docker compose up` sem intervenção manual.
4. O Worker do `OrderService` sobe sem erros e processa mensagens da fila.
5. Mensagens que não encontram o `OrderId` são descartadas com log de aviso, sem derrubar o Worker.
6. O README e o `docs/08-guia-pratico.md` mostram o fluxo correto: `GET /orders/{id}` retornando `status: "Completed"` ou `"Failed"` após a saga terminar.
7. Os testes de integração `HappyPathTests` e `CompensationTests` assertam `order.status` via `GET /orders/{id}`, cobrindo ambos os estados terminais.
8. O script `happy-path-demo.sh` verifica `GET /orders/{orderId}` em cada cenário e falha se o status do pedido não corresponder ao estado terminal da saga.
