# Tasks: mt-integration-tests

**Feature:** mt-integration-tests
**Milestone:** M10 - Migração MassTransit

## Resumo

5 tarefas. Depende de `mt-program-config` (arquitetura funcionando) e `mt-infra-docker` (docker-compose atualizado). Tarefas T2-T4 são independentes entre si.

---

## T1 — Atualizar DockerComposeFixture

**Arquivo:** `tests/IntegrationTests/DockerComposeFixture.cs` (ou equivalente)

**O que fazer:**

1. Remover `saga-orchestrator` da lista de serviços esperados no startup
2. Ajustar serviços aguardados: `order-service`, `payment-service`, `inventory-service`, `shipping-service`, `postgres`, `localstack`
3. Atualizar healthchecks conforme nova configuração
4. Verificar que timeout de startup é adequado para 4 serviços

**Verificação:** `dotnet test` inicializa a fixture sem erros.

---

## T2 — Adaptar cenários existentes de happy path e falhas

**Arquivo:** `tests/IntegrationTests/SagaTests.cs` (ou equivalente)

**O que fazer:**

1. Substituir verificações em `GET /sagas/{id}` por `GET /orders/{id}`
2. Atualizar asserções de status: verificar `status: Completed` ou `status: Failed`
3. Remover asserções sobre `idempotency_keys` (tabela não existe mais)
4. Atualizar cenário de pedido duplicado: verificar `status 409` ou mesmo pedido retornado

**Verificação:** Cenários de happy path, payment fail e inventory fail passam.

---

## T3 — Adicionar cenário: compensação completa (Shipping falha)

**Arquivo:** `tests/IntegrationTests/SagaTests.cs`

**O que fazer:**

Criar teste `Shipping_Falha_Compensa_Inventory_E_Payment`:
1. Configurar cliente/produto para que Payment e Inventory passem, mas Shipping falhe
2. Criar pedido via `POST /orders`
3. Aguardar processamento (poll com timeout de 10s)
4. Verificar `GET /orders/{id}` → `status: Failed`
5. Verificar que reservation foi cancelada (via endpoint de inventário ou direto no banco)
6. Verificar que pagamento foi cancelado (via endpoint ou log)

**Verificação:** Teste passa com compensação completa verificada.

---

## T4 — Adaptar cenário de DLQ

**Arquivo:** `tests/IntegrationTests/DlqTests.cs` (ou equivalente)

**O que fazer:**

1. Atualizar nome da fila DLQ de `*-dlq` para `order-saga_error`
2. Atualizar `GET /dlq` para buscar da fila correta
3. Verificar que mensagem malformada enviada diretamente ao SQS aparece na DLQ após retries
4. Verificar `POST /dlq/redrive` move mensagem de volta

**Verificação:** Cenário de DLQ passa.

---

## T5 — Adaptar cenário de concorrência

**Arquivo:** `tests/IntegrationTests/ConcurrencyTests.cs` (ou equivalente)

**O que fazer:**

1. Resetar estoque para 2 via `POST /inventory/reset`
2. Disparar 5 pedidos simultâneos via `Task.WhenAll`
3. Aguardar todos terminarem (poll com timeout de 30s)
4. Verificar `GET /orders/{id}` para cada: exatamente 2 `Completed`, 3 `Failed`
5. Verificar que estoque não ficou negativo

**Verificação:** Teste passa com `INVENTORY_LOCKING_MODE=pessimistic` e `optimistic`.

---

## Dependências

```
mt-program-config (fluxo end-to-end funcionando) → T1
mt-infra-docker (docker-compose atualizado) → T1
T1 → T2, T3, T4, T5 (fixture precisa iniciar)
T2, T3, T4, T5 independentes entre si
```
