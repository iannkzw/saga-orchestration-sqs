# Infra Docker Compose - Specification

## Problem Statement

A PoC precisa de infraestrutura local reproduzivel para que qualquer desenvolvedor consiga subir o ambiente completo com um unico comando. Sem isso, nao ha como demonstrar ou testar os padroes de saga orquestrada.

## Goals

- [ ] Ambiente completo sobe com `docker compose up` sem dependencias externas
- [ ] Filas SQS criadas automaticamente no LocalStack na inicializacao
- [ ] PostgreSQL acessivel com schemas iniciais criados automaticamente
- [ ] Health checks garantem que todos os containers estao saudaveis antes de aceitar trafego

## Out of Scope

| Feature | Reason |
| --- | --- |
| Deploy em cloud AWS | PoC 100% local |
| Dockerfiles otimizados (multi-stage, distroless) | Clareza sobre otimizacao — M1 foca em funcionalidade |
| Volumes persistentes para dados | PoC descartavel, dados resetam a cada `docker compose down` |
| TLS/HTTPS entre servicos | Sem autenticacao no escopo da PoC |
| Nginx/reverse proxy | Acesso direto aos servicos via portas mapeadas |

---

## User Stories

### P1: Docker Compose Unificado ⭐ MVP

**User Story**: Como desenvolvedor, quero executar `docker compose up` e ter todos os servicos, banco de dados e LocalStack rodando, para poder testar a PoC sem configuracao manual.

**Why P1**: Sem infraestrutura, nenhuma outra feature pode ser desenvolvida ou testada.

**Acceptance Criteria**:

1. WHEN `docker compose up -d` e executado THEN todos os containers (localstack, postgres, order-service, saga-orchestrator, payment-service, inventory-service, shipping-service) SHALL iniciar sem erros
2. WHEN todos os containers estao UP THEN cada servico .NET SHALL estar acessivel na sua porta mapeada
3. WHEN `docker compose down` e executado THEN todos os containers SHALL ser removidos sem residuos

**Independent Test**: Executar `docker compose up -d && docker compose ps` e verificar que todos os 7 containers estao com status healthy/running.

---

### P1: Criacao Automatica de Filas SQS ⭐ MVP

**User Story**: Como desenvolvedor, quero que as filas SQS sejam criadas automaticamente no LocalStack ao subir o ambiente, para nao precisar criar filas manualmente.

**Why P1**: Sem filas, os servicos nao conseguem se comunicar — e o core do padrao saga.

**Acceptance Criteria**:

1. WHEN o container LocalStack esta healthy THEN um init script SHALL criar todas as filas SQS necessarias (order-commands, payment-commands, payment-replies, inventory-commands, inventory-replies, shipping-commands, shipping-replies, saga-commands)
2. WHEN as filas sao criadas THEN cada fila principal SHALL ter uma Dead Letter Queue associada (sufixo -dlq)
3. WHEN `awslocal sqs list-queues` e executado THEN SHALL listar todas as filas criadas (principais + DLQs)

**Independent Test**: `docker compose up -d localstack && sleep 5 && docker compose exec localstack awslocal sqs list-queues` deve listar todas as filas.

---

### P1: PostgreSQL com Init Scripts ⭐ MVP

**User Story**: Como desenvolvedor, quero que o PostgreSQL suba com schemas e databases pre-criados, para que os servicos consigam conectar e persistir dados imediatamente.

**Why P1**: Servicos precisam de banco para persistir estado de sagas, pedidos e idempotencia.

**Acceptance Criteria**:

1. WHEN o container PostgreSQL esta healthy THEN os databases para cada servico SHALL estar criados (saga_db)
2. WHEN os databases estao criados THEN o init script SHALL ter executado sem erros
3. WHEN um servico .NET conecta ao PostgreSQL THEN a conexao SHALL ser estabelecida com sucesso

**Independent Test**: `docker compose up -d postgres && docker compose exec postgres psql -U saga -d saga_db -c '\dt'` deve conectar com sucesso.

---

### P2: Health Checks em Todos os Containers

**User Story**: Como desenvolvedor, quero que o Docker Compose tenha health checks configurados em todos os containers, para saber quando o ambiente esta realmente pronto.

**Why P2**: Importante para confiabilidade mas os servicos .NET ainda nao existem no M1 — health checks dos servicos serao placeholders.

**Acceptance Criteria**:

1. WHEN o container LocalStack esta rodando THEN o health check SHALL validar que o endpoint /_localstack/health retorna 200
2. WHEN o container PostgreSQL esta rodando THEN o health check SHALL validar que `pg_isready` retorna sucesso
3. WHEN `docker compose ps` e executado THEN todos os containers de infraestrutura SHALL mostrar status "(healthy)"

**Independent Test**: `docker compose up -d && docker compose ps` — coluna STATUS deve mostrar "(healthy)" para localstack e postgres.

---

## Edge Cases

- WHEN o LocalStack demora mais que o esperado para iniciar THEN os servicos .NET SHALL aguardar via `depends_on: condition: service_healthy`
- WHEN a porta 4566 (LocalStack) ja esta em uso THEN `docker compose up` SHALL falhar com mensagem clara de porta em conflito
- WHEN o init script de filas SQS falha THEN o container LocalStack SHALL reportar status unhealthy
- WHEN o PostgreSQL nao aceita conexoes ainda THEN os servicos dependentes SHALL aguardar via health check condition

---

## Requirement Traceability

| Requirement ID | Story | Phase | Status |
| --- | --- | --- | --- |
| INFRA-01 | P1: Docker Compose Unificado | Execute | Implementing |
| INFRA-02 | P1: Docker Compose Unificado - Portas | Execute | Implementing |
| INFRA-03 | P1: Criacao Automatica Filas SQS | Execute | Implementing |
| INFRA-04 | P1: Filas SQS com DLQs | Execute | Implementing |
| INFRA-05 | P1: PostgreSQL Init Scripts | Execute | Implementing |
| INFRA-06 | P2: Health Checks Infra | Execute | Implementing |
| INFRA-07 | Edge: depends_on com health condition | Execute | Implementing |

**Coverage:** 7 total, 7 mapped to tasks, 0 unmapped

---

## Success Criteria

- [ ] `docker compose up -d` sobe ambiente completo sem erros em <60s
- [ ] `awslocal sqs list-queues` lista todas as filas (principais + DLQs)
- [ ] PostgreSQL acessivel e com database saga_db criado
- [ ] `docker compose ps` mostra containers de infra como healthy
