# Feature: mt-infra-docker

**Milestone:** M10 - Migração MassTransit
**Status:** PLANNED

## Objetivo

Atualizar o `docker-compose.yml` para refletir a nova arquitetura com 4 contêineres (removendo `saga-orchestrator`), configurar as variáveis de ambiente MassTransit/SQS, e garantir que o `LocalStack` está configurado corretamente para o MassTransit criar filas e topics SNS automaticamente.

## Mudanças no docker-compose.yml

### Serviços Removidos

- `saga-orchestrator` — eliminado com a fusão no OrderService

### Serviços Mantidos (com atualização)

| Serviço | Container Name | Porta | Mudanças |
|---------|---------------|-------|---------|
| `order-service` | `saga-order-service` | `5001` | Absorve config do orquestrador, adiciona vars MassTransit |
| `payment-service` | `saga-payment-service` | `5002` | Remove vars de fila manual, adiciona `AWS_SQS_SERVICE_URL` |
| `inventory-service` | `saga-inventory-service` | `5003` | Remove `INVENTORY_LOCKING_ENABLED`, mantém `INVENTORY_LOCKING_MODE` |
| `shipping-service` | `saga-shipping-service` | `5004` | Remove vars de fila manual |
| `localstack` | `saga-localstack` | `4566` | Adicionar `SQS,SNS` em `SERVICES` |
| `postgres` | `saga-postgres` | `5432` | Sem mudança |

### Variáveis de Ambiente Novas (todos os serviços)

```yaml
environment:
  AWS_SQS_SERVICE_URL: http://localstack:4566
  AWS_SNS_SERVICE_URL: http://localstack:4566
  AWS_ACCESS_KEY_ID: test
  AWS_SECRET_ACCESS_KEY: test
  AWS_REGION: us-east-1
```

### Variáveis Removidas

```yaml
# Removidas do order-service:
SAGA_ORCHESTRATOR_URL: http://saga-orchestrator:5000

# Removidas de todos os serviços de domínio:
PAYMENT_COMMANDS_QUEUE: payment-commands
PAYMENT_REPLIES_QUEUE: payment-replies
INVENTORY_COMMANDS_QUEUE: inventory-commands
# ... etc
ORDER_STATUS_UPDATES_QUEUE: order-status-updates
```

### LocalStack: SNS habilitado

O MassTransit usa SNS para fan-out de eventos. Atualizar `SERVICES`:

```yaml
localstack:
  environment:
    SERVICES: sqs,sns  # adicionar sns
```

## Dockerfile por Serviço

Sem mudanças nos `Dockerfile` (apenas código .NET muda). Verificar que o healthcheck do `LocalStack` está correto:

```yaml
healthcheck:
  test: ["CMD", "awslocal", "sqs", "list-queues"]
  interval: 5s
  timeout: 10s
  retries: 10
  start_period: 10s
```

> Nota: o `awslocal` no healthcheck pode ser substituído por `curl -f http://localhost:4566/_localstack/health` se `awslocal` não estiver disponível.

## Ordem de Startup (depends_on)

```yaml
order-service:
  depends_on:
    postgres:
      condition: service_healthy
    localstack:
      condition: service_healthy

payment-service:
  depends_on:
    localstack:
      condition: service_healthy

# idem para inventory-service e shipping-service
```

## Critérios de Aceite

1. `docker compose up` sobe 4 serviços de aplicação + postgres + localstack (6 contêineres total)
2. Nenhuma referência ao contêiner `saga-orchestrator` no arquivo
3. LocalStack tem `sqs,sns` em `SERVICES`
4. Todos os serviços têm `AWS_SQS_SERVICE_URL` configurado
5. `docker compose ps` mostra todos os serviços healthy
