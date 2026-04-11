# Feature: mt-integration-tests

**Milestone:** M10 - Migração MassTransit
**Status:** PLANNED

## Objetivo

Adaptar os testes de integração existentes (feature `integration-tests`) para a nova arquitetura MassTransit: 4 contêineres, sem `SagaOrchestrator`, endpoints e comportamentos atualizados.

## Estado Atual dos Testes

Os testes existentes usam `DockerComposeFixture` para subir os serviços e testam via HTTP. Cenários cobertos:
1. Happy path completo
2. Falha no pagamento (compensação)
3. Falha no inventário (compensação com cancel payment)
4. Pedido duplicado (idempotência)
5. Concorrência (5 pedidos simultâneos, estoque=2)
6. DLQ (mensagem inválida vai para DLQ)
7. Redrive de DLQ

## Mudanças Necessárias

### 1. DockerComposeFixture

- Remover `saga-orchestrator` da lista de serviços esperados
- Ajustar timeout de startup (4 serviços em vez de 5)
- Atualizar `SERVICES` do LocalStack para incluir SNS

### 2. Cenário: Pedido duplicado

**Antes:** verificava `IdempotencyStore` (tabela `idempotency_keys`)
**Depois:** verificar que dois `POST /orders` com mesmo `OrderId` resultam em um único pedido (MassTransit saga correlation rejeita duplicata)

### 3. Cenário: Estado da saga

**Antes:** `GET /sagas/{id}` retornava histórico de transições
**Depois:** `GET /orders/{id}` retorna `status` que reflete o estado da saga (não há mais endpoint `/sagas`)

### 4. Cenário: DLQ

**Antes:** DLQ era fila `*-dlq` explícita com nome hardcoded
**Depois:** DLQ é `order-saga_error` criada pelo MassTransit. `GET /dlq` aponta para essa fila.

### 5. Novo Cenário: Compensação completa (Shipping falha)

Testar cadeia completa de compensação: Order → Payment OK → Inventory OK → Shipping FAIL → Cancel Inventory → Cancel Payment → Failed.

## Cenários de Teste Alvo

| Cenário | Endpoint testado | Resultado esperado |
|---------|-----------------|-------------------|
| Happy path | `POST /orders` → `GET /orders/{id}` | `status: Completed` |
| Payment falha | `POST /orders` com cliente blacklistado | `status: Failed` |
| Inventory falha | `POST /orders` sem estoque | `status: Failed`, payment cancelado |
| Shipping falha | `POST /orders` com endereço inválido | `status: Failed`, inventory + payment cancelados |
| Pedido duplicado | 2x `POST /orders` mesmo body | 1 pedido criado, 2ª requisição retorna 409 ou o mesmo pedido |
| Concorrência | 5 pedidos simultâneos, estoque=2 | Exatamente 2 `Completed`, 3 `Failed` |
| DLQ | Mensagem malformada via SQS direto | Aparece em `GET /dlq` |

## Critérios de Aceite

1. Todos os 7 cenários passam
2. `docker compose up` com fixture sobe em menos de 60s
3. Testes não dependem do serviço `saga-orchestrator`
4. Fixture limpa estado entre testes (reset de estoque, banco)
