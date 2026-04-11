# Tasks: mt-infra-docker

**Feature:** mt-infra-docker
**Milestone:** M10 - Migração MassTransit

## Resumo

3 tarefas. Pode ser executada em paralelo com as features de código — é independente da implementação. Deve ser finalizada antes de `mt-program-config` (validação end-to-end).

---

## T1 — Remover serviço saga-orchestrator e variáveis obsoletas

**Arquivo:** `docker-compose.yml`

**O que fazer:**

1. Remover bloco completo do serviço `saga-orchestrator`
2. Remover `SAGA_ORCHESTRATOR_URL` do `order-service`
3. Remover variáveis de filas manuais de todos os serviços:
   - `*_COMMANDS_QUEUE`, `*_REPLIES_QUEUE`, `ORDER_STATUS_UPDATES_QUEUE`
4. Substituir `INVENTORY_LOCKING_ENABLED=true` por `INVENTORY_LOCKING_MODE=pessimistic` (se ainda não feito via `optimistic-locking`)

**Verificação:** `docker compose config` sem referências ao `saga-orchestrator`.

---

## T2 — Adicionar variáveis MassTransit/SQS e habilitar SNS no LocalStack

**Arquivo:** `docker-compose.yml`

**O que fazer:**

1. Adicionar em todos os serviços de aplicação:
   ```yaml
   AWS_SQS_SERVICE_URL: http://localstack:4566
   AWS_SNS_SERVICE_URL: http://localstack:4566
   AWS_ACCESS_KEY_ID: test
   AWS_SECRET_ACCESS_KEY: test
   AWS_REGION: us-east-1
   ```

2. No serviço `localstack`, atualizar `SERVICES`:
   ```yaml
   SERVICES: sqs,sns
   ```

3. Adicionar `LOCALSTACK_ACKNOWLEDGE_ACCOUNT_REQUIREMENT=1` se não existir (lesson learned M5)

**Verificação:** `docker compose up localstack` e `awslocal sns list-topics --region us-east-1` sem erros de serviço não disponível.

---

## T3 — Atualizar depends_on e healthchecks

**Arquivo:** `docker-compose.yml`

**O que fazer:**

1. Remover `depends_on: saga-orchestrator` de qualquer serviço que o referencie
2. Adicionar `depends_on` correto para todos os serviços de aplicação:
   - `order-service`: depende de `postgres` (healthy) e `localstack` (healthy)
   - `payment-service`, `inventory-service`, `shipping-service`: dependem de `localstack` (healthy)
3. Verificar healthcheck do LocalStack (usar `curl` se `awslocal` não disponível)
4. Verificar healthcheck do PostgreSQL

**Verificação:** `docker compose up` na ordem correta sem race conditions. `docker compose ps` mostra todos healthy.

---

## Dependências

```
T1 → T2 (limpar antes de adicionar)
T2 → T3 (services configurados antes dos healthchecks)
mt-cleanup coordena com T1 (remoção de variáveis obsoletas)
```
