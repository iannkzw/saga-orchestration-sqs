# Design: order-status-sync

## Decisões Arquiteturais

---

### D1 — Mecanismo de notificação do SagaOrchestrator para o OrderService

**Problema:** O `SagaOrchestrator` precisa notificar o `OrderService` quando a saga atinge um estado terminal, sem introduzir acoplamento direto entre os serviços.

**Opções avaliadas:**

| Opção | Prós | Contras |
|-------|------|---------|
| **SQS (push via nova fila)** | Desacoplado, assíncrono, segue padrão arquitetural existente, tolerante a falhas temporárias do OrderService | Mensagem pode atrasar em cenários de alto volume |
| HTTP callback do SagaOrchestrator para OrderService | Simples, sem nova fila | Cria dependência de disponibilidade do OrderService no momento do callback; quebra o modelo de desacoplamento |
| OrderService faz polling no SagaOrchestrator | Sem mudança no SagaOrchestrator | Polling é ineficiente; race condition se GET /orders chega entre polls; lógica de negócio vaza para camada de leitura |
| Outbox Pattern (SagaOrchestrator escreve em tabela antes de publicar) | Garante exactly-once, sem dual-write | Complexidade adicional (tabela outbox + job de publicação); desnecessário para PoC |

**Decisão: SQS via nova fila `order-status-updates`**

Razões:
- Alinha com o padrão estabelecido: todo fluxo de notificação entre serviços usa SQS.
- O `OrderService` continua completamente desacoplado do `SagaOrchestrator` em runtime.
- O Worker do `OrderService` segue exatamente o mesmo padrão dos Workers existentes (Payment/Inventory/Shipping).
- Tolerante a reinicializações: se o `OrderService` estiver down quando a notificação for publicada, ela permanece na fila.
- O Outbox é descartado por ser overkill para PoC — best-effort é suficiente aqui.

---

### D2 — Onde publicar a notificação no SagaOrchestrator

**Problema:** O estado terminal é atingido em dois pontos distintos do `Worker.cs`:
- `HandleSuccessAsync` (linhas 158–193): quando o último sucesso (`ShippingReply`) leva ao estado `Completed`
- `HandleCompensationReplyAsync` (linhas 237–274): quando a última compensação completa leva ao estado `Failed`

**Decisão: publicar no mesmo método, após `db.SaveChangesAsync`**

```
// Pseudocódigo — HandleSuccessAsync
if (SagaStateMachine.IsTerminal(nextState))
{
    await db.SaveChangesAsync(ct);               // persiste estado terminal
    await PublishSagaTerminatedAsync(saga, ct);  // notifica OrderService
}

// Pseudocódigo — HandleCompensationReplyAsync
if (SagaStateMachine.IsTerminal(newState))
{
    await db.SaveChangesAsync(ct);
    await PublishSagaTerminatedAsync(saga, ct);
}
```

Razões:
- Centraliza a publicação próxima à persistência para minimizar janela de inconsistência.
- Evita duplicação: um único método privado `PublishSagaTerminatedAsync` é chamado dos dois pontos.
- Falha na publicação não reverte o `SaveChangesAsync` (best-effort); a saga permanece no estado correto no banco.

---

### D3 — Contrato da notificação

**Problema:** Como representar a mensagem enviada pelo SagaOrchestrator e consumida pelo OrderService sem acoplamento de strings.

**Decisão: novo tipo `SagaTerminatedNotification` em `Shared/Contracts/Notifications/`**

```csharp
// Shared/Contracts/Notifications/SagaTerminatedNotification.cs
public record SagaTerminatedNotification(
    Guid SagaId,
    Guid OrderId,
    string TerminalState   // "Completed" | "Failed"
);
```

Razões:
- Segue a convenção de contracts já existente (`Shared/Contracts/Commands/`, `Shared/Contracts/Replies/`).
- Ambos os serviços referenciam `Shared.csproj` — zero duplicação.
- `TerminalState` como `string` evita dependência circular de enum entre projetos (o `SagaState` é enum do `SagaOrchestrator`).

**MessageAttribute de roteamento:** não é necessário — a fila `order-status-updates` tem apenas um tipo de mensagem. O Worker não precisa de dispatch por `CommandType`.

---

### D4 — Estrutura do Worker no OrderService

**Problema:** Como estruturar o novo Worker para ser consistente com o padrão existente.

**Decisão: replicar o padrão do `InventoryService.Worker`**

```csharp
// OrderService/Worker.cs
public class Worker(IAmazonSQS sqs, OrderDbContext db, ILogger<Worker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queueUrl = (await sqs.GetQueueUrlAsync(SqsConfig.OrderStatusUpdates, stoppingToken)).QueueUrl;

        while (!stoppingToken.IsCancellationRequested)
        {
            var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                WaitTimeSeconds = 2,
                MaxNumberOfMessages = 10,
                MessageAttributeNames = ["All"]
            }, stoppingToken);

            if (response?.Messages is { Count: > 0 })
            {
                var tasks = response.Messages.Select(msg =>
                    ProcessMessageAsync(msg, queueUrl, stoppingToken));
                await Task.WhenAll(tasks);
            }

            await Task.Delay(200, stoppingToken);
        }
    }
}
```

**`ProcessMessageAsync` deve:**
1. Deserializar body como `SagaTerminatedNotification`
2. Buscar `Order` pelo `OrderId`
3. Se não encontrado: logar warning + deletar mensagem (não relançar)
4. Mapear `TerminalState` → `OrderStatus` (`"Completed"` → `Completed`, qualquer outro → `Failed`)
5. Atualizar `order.Status` e `order.UpdatedAt = DateTime.UtcNow`
6. Persistir via `db.SaveChangesAsync`
7. Deletar mensagem da fila

---

### D5 — Criação da fila no LocalStack

**Decisão: adicionar fila ao `init-queues.sh` existente, seguindo o padrão das demais**

```bash
# Fila principal
awslocal sqs create-queue --queue-name order-status-updates \
  --attributes RedrivePolicy='{"deadLetterTargetArn":"arn:aws:sqs:us-east-1:000000000000:order-status-updates-dlq","maxReceiveCount":"3"}'

# DLQ correspondente
awslocal sqs create-queue --queue-name order-status-updates-dlq
```

A DLQ deve ser criada **antes** da fila principal (mesmo padrão do script atual).

---

### D6 — Impacto em GET /orders/{id}

**Decisão: nenhuma mudança no endpoint GET /orders/{id}**

O endpoint já retorna `order.Status` do banco. Após o Worker atualizar o status, a próxima chamada `GET /orders/{id}` refletirá o valor correto automaticamente.

O campo `sagaState` embarcado (vindo da consulta HTTP ao orquestrador) permanece como está — é redundante após esta feature, mas mantê-lo não causa dano e preserva compatibilidade.
