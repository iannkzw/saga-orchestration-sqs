# Guia Pratico: Reproduzindo e Testando Cenarios

Este guia e um passo a passo para subir o ambiente e reproduzir todos os cenarios demonstrados no projeto.

---

## Pre-requisitos

| Ferramenta | Versao minima | Verificacao |
|------------|---------------|-------------|
| Docker | 24.x | `docker --version` |
| Docker Compose | 2.x | `docker compose version` |
| curl | qualquer | `curl --version` |
| .NET 10 SDK | 10.0 (opcional) | `dotnet --version` |

> O .NET SDK e necessario apenas para rodar os servicos localmente sem Docker. Para o fluxo completo com Docker, apenas Docker e necessario.

---

## Subindo o Ambiente

```bash
# Clonar (se ainda nao fez)
git clone <url-do-repositorio>
cd saga-orchestration-dotnet-sqs

# Subir todos os containers (LocalStack, PostgreSQL, 5 servicos .NET)
docker compose up --build -d
```

Aguarde todos os containers ficarem `healthy`. Verifique com:

```bash
docker compose ps
```

Saida esperada (todos `healthy` ou `running`):

```
NAME                STATUS          PORTS
localstack          running         ...4566->4566
postgres            healthy         ...5432->5432
order-service       healthy         ...5001->5001
saga-orchestrator   healthy         ...5002->5002
payment-service     running
inventory-service   running
shipping-service    running
```

### Verificando health checks individualmente

```bash
curl http://localhost:5001/health  # OrderService
curl http://localhost:5002/health  # SagaOrchestrator
```

Resposta esperada:
```json
{
  "status": "healthy",
  "service": "OrderService",
  "connections": {
    "sqs": true,
    "postgres": true
  }
}
```

---

## Cenario 1: Happy Path

O fluxo completo sem falhas: Order → Payment → Inventory → Shipping → Completed.

### Passo 1: Criar um pedido

```bash
curl -X POST http://localhost:5001/orders \
  -H "Content-Type: application/json" \
  -d '{
    "totalAmount": 99.90,
    "items": [
      {"productId": "PROD-001", "quantity": 1, "unitPrice": 99.90}
    ]
  }'
```

Resposta:
```json
{
  "orderId": "aaaaaaaa-...",
  "sagaId":  "bbbbbbbb-...",
  "status":  "Processing"
}
```

Anote os IDs retornados.

### Passo 2: Acompanhar o progresso do pedido

```bash
curl http://localhost:5001/orders/{orderId}
```

### Passo 3: Ver o estado da saga diretamente

```bash
curl http://localhost:5002/sagas/{sagaId}
```

Resposta com saga completa:
```json
{
  "sagaId": "bbbbbbbb-...",
  "orderId": "aaaaaaaa-...",
  "state": "Completed",
  "transitions": [
    {"from": "Pending",            "to": "PaymentProcessing",   "triggeredBy": "SagaCreated",      "timestamp": "..."},
    {"from": "PaymentProcessing",  "to": "InventoryReserving",  "triggeredBy": "PaymentReplies",   "timestamp": "..."},
    {"from": "InventoryReserving", "to": "ShippingScheduling",  "triggeredBy": "InventoryReplies", "timestamp": "..."},
    {"from": "ShippingScheduling", "to": "Completed",           "triggeredBy": "ShippingReplies",  "timestamp": "..."}
  ]
}
```

---

## Cenario 2: Falha com Compensacao

Simule falhas em cada ponto usando o header `X-Simulate-Failure`.

### Falha no Payment (sem compensacoes)

```bash
curl -X POST http://localhost:5001/orders \
  -H "Content-Type: application/json" \
  -H "X-Simulate-Failure: payment" \
  -d '{"totalAmount": 50.00, "items": [{"productId": "PROD-002", "quantity": 1, "unitPrice": 50.00}]}'
```

Estado final esperado: `Failed` (direto, sem compensacoes necessarias)

Transicoes esperadas:
```
Pending → PaymentProcessing → Failed
```

### Falha no Inventory (compensa Payment)

```bash
curl -X POST http://localhost:5001/orders \
  -H "Content-Type: application/json" \
  -H "X-Simulate-Failure: inventory" \
  -d '{"totalAmount": 75.00, "items": [{"productId": "PROD-003", "quantity": 1, "unitPrice": 75.00}]}'
```

Transicoes esperadas:
```
Pending → PaymentProcessing → InventoryReserving → PaymentRefunding → Failed
```

### Falha no Shipping (compensa Inventory + Payment)

```bash
curl -X POST http://localhost:5001/orders \
  -H "Content-Type: application/json" \
  -H "X-Simulate-Failure: shipping" \
  -d '{"totalAmount": 120.00, "items": [{"productId": "PROD-004", "quantity": 1, "unitPrice": 120.00}]}'
```

Transicoes esperadas:
```
Pending → PaymentProcessing → InventoryReserving → ShippingScheduling
       → InventoryReleasing → PaymentRefunding → Failed
```

---

## Cenario 3: Inspecionando as DLQs

Para ver mensagens nas DLQs, voce precisa provocar falhas que o worker nao consiga processar. Em condicoes normais (servicos funcionando), as DLQs ficam vazias.

### Verificar estado atual das DLQs

```bash
curl http://localhost:5002/dlq
```

Resposta (DLQs vazias):
```json
{
  "payment-commands-dlq":   {"count": 0, "messages": []},
  "inventory-commands-dlq": {"count": 0, "messages": []},
  "shipping-commands-dlq":  {"count": 0, "messages": []},
  "payment-replies-dlq":    {"count": 0, "messages": []}
}
```

### Forcando mensagens na DLQ (simulacao)

Para testar o redrive sem derrubar servicos, voce pode enviar diretamente uma mensagem malformada via CLI do LocalStack:

```bash
docker exec localstack awslocal sqs send-message \
  --queue-url http://sqs.us-east-1.localhost.localstack.cloud:4566/000000000000/payment-commands \
  --message-body '{"mensagem": "invalida"}' \
  --message-attributes '{"CommandType": {"DataType": "String", "StringValue": "ProcessPayment"}}'
```

Apos 3 falhas de processamento (o worker rejeita mensagens invalidas), a mensagem migra para a DLQ automaticamente.

---

## Cenario 4: Redrive de Mensagens da DLQ

### Listar mensagens disponíveis

```bash
curl http://localhost:5002/dlq | jq '.["payment-commands-dlq"]'
```

### Reenviar para reprocessamento

```bash
curl -X POST http://localhost:5002/dlq/redrive \
  -H "Content-Type: application/json" \
  -d '{
    "queueName": "payment-commands-dlq",
    "receiptHandle": "AQEBwJnK..."
  }'
```

> O `receiptHandle` vem da resposta do `GET /dlq`. Copie o valor do campo `receiptHandle` da mensagem desejada.

Resposta esperada:
```json
{
  "redriven": true,
  "fromDlq": "payment-commands-dlq",
  "toQueue": "payment-commands",
  "messageId": "abc-123"
}
```

---

## Cenario 5: Observando Traces no Console

Os traces OpenTelemetry sao exibidos no stdout de cada container.

### Ver traces do SagaOrchestrator

```bash
# Filtrar linhas de Activity (spans OTel)
docker compose logs saga-orchestrator | grep -E "Activity\.(TraceId|SpanId|DisplayName|Duration)"
```

Saida esperada:
```
Activity.TraceId:      4bf92f3577b34da6a3ce929d0e0e4736
Activity.SpanId:       00f067aa0ba902b7
Activity.DisplayName:  send ProcessPayment
Activity.Kind:         Producer
Activity.Duration:     00:00:00.0124567
```

### Ver traces do PaymentService

```bash
docker compose logs payment-service | grep -E "Activity\."
```

Observe que o `TraceId` e o **mesmo** do Orchestrator — confirmando que os spans estao conectados.

### Seguir logs em tempo real

```bash
# Em terminais separados, acompanhar a saga em tempo real:
docker compose logs -f saga-orchestrator &
docker compose logs -f payment-service &

# Entao criar um pedido
curl -X POST http://localhost:5001/orders \
  -H "Content-Type: application/json" \
  -d '{"totalAmount": 99.90, "items": [{"productId": "PROD-001", "quantity": 1, "unitPrice": 99.90}]}'
```

---

## Cenario 6: Verificando Idempotencia

A idempotencia garante que mensagens duplicadas nao causem processamento duplicado.

### Verificar logs de idempotency hits

```bash
docker compose logs payment-service | grep "Idempotency"
```

Em condicoes normais (sem duplicatas forcadas), voce vera apenas salvamentos:
```
Idempotency save: chave 550e8400-...-payment salva para saga 550e8400-...
```

### Simular replay de mensagem

Para verificar a idempotencia funcionando, voce pode re-enviar o mesmo comando para a fila:

```bash
# Buscar o body de uma mensagem ja processada (do log ou banco)
# Re-enviar com a mesma IdempotencyKey
docker exec localstack awslocal sqs send-message \
  --queue-url http://sqs.us-east-1.localhost.localstack.cloud:4566/000000000000/payment-commands \
  --message-body '{"SagaId": "...", "IdempotencyKey": "...-payment", "Amount": 99.90}' \
  --message-attributes '{"CommandType": {"DataType": "String", "StringValue": "ProcessPayment"}}'
```

Nos logs do PaymentService, voce vera:
```
Idempotency hit: chave ...-payment ja processada, retornando resultado anterior
```

Nenhuma nova cobranca sera realizada — o resultado anterior e retornado diretamente.

---

## Troubleshooting

### Containers nao ficam `healthy`

```bash
# Verificar logs de um container especifico
docker compose logs order-service

# Reiniciar um container
docker compose restart order-service
```

**Causa comum:** LocalStack ou PostgreSQL ainda nao estava pronto quando o servico .NET iniciou. Os health checks tentam novamente automaticamente — aguarde 30-60 segundos.

### Porta ja em uso

```bash
# Ver quem esta usando a porta 5001
netstat -ano | findstr :5001  # Windows
lsof -i :5001                 # Linux/Mac

# Parar todos os containers
docker compose down
```

### Banco de dados nao inicializado

```bash
# Verificar logs do postgres
docker compose logs postgres

# Verificar init scripts
ls infra/postgres/
```

### Resetar tudo (estado limpo)

```bash
# Para containers e remove volumes (APAGA TODOS OS DADOS)
docker compose down -v

# Sobe novamente do zero
docker compose up --build -d
```

### Filas SQS nao criadas

```bash
# Verificar se o init script rodou
docker compose logs localstack | grep "Todas as filas"

# Listar filas manualmente
docker exec localstack awslocal sqs list-queues
```

---

## Comandos Uteis de Referencia

```bash
# Subir tudo
docker compose up --build -d

# Ver status
docker compose ps

# Logs em tempo real
docker compose logs -f

# Parar tudo (preserva volumes)
docker compose down

# Parar e limpar tudo
docker compose down -v

# Happy path rapido
curl -s -X POST http://localhost:5001/orders \
  -H "Content-Type: application/json" \
  -d '{"totalAmount": 99.90, "items": [{"productId": "PROD-001", "quantity": 1, "unitPrice": 99.90}]}' | jq .

# Falha com compensacao completa
curl -s -X POST http://localhost:5001/orders \
  -H "Content-Type: application/json" \
  -H "X-Simulate-Failure: shipping" \
  -d '{"totalAmount": 99.90, "items": [{"productId": "PROD-001", "quantity": 1, "unitPrice": 99.90}]}' | jq .

# Ver todas as DLQs
curl -s http://localhost:5002/dlq | jq .
```
