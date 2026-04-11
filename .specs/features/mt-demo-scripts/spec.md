# Feature: mt-demo-scripts

**Milestone:** M10 - Migração MassTransit
**Status:** PLANNED

## Objetivo

Atualizar os scripts de demonstração (`scripts/`) para a nova arquitetura com 4 serviços MassTransit, removendo referências ao `SagaOrchestrator` e ajustando portas, endpoints e comportamentos.

## Scripts Existentes

| Script | Status | Ação |
|--------|--------|------|
| `happy-path-demo.sh` | Atualizar | Ajustar endpoints, remover refs ao orquestrador |
| `concurrent-saga-demo.sh` | Atualizar | Manter `--mode` para locking, atualizar endpoints |
| `setup-infra.sh` / `init-sqs.sh` | Remover ou esvaziar | Filas criadas automaticamente pelo MassTransit |

## Mudanças por Script

### `happy-path-demo.sh`

**Mudanças:**
- Remover chamadas para `http://localhost:5000` (porta do SagaOrchestrator)
- Remover verificação de saúde do `SagaOrchestrator`
- Atualizar polling de status: `GET http://localhost:5001/orders/{id}` (campo `status`)
- Substituir verificação de `GET /sagas/{id}` por `GET /orders/{id}`
- Atualizar comentários explicativos para mencionar MassTransit

**Cenários mantidos:**
1. Happy path completo
2. Falha no pagamento
3. Falha no inventário (compensação)
4. Falha no shipping (compensação completa)

### `concurrent-saga-demo.sh`

**Mudanças:**
- Remover referência ao SagaOrchestrator no healthcheck
- Atualizar polling de status para `GET /orders/{id}`
- Flag `--mode` mapeia para `INVENTORY_LOCKING_MODE` (já atualizado via `optimistic-locking`)
- Atualizar contagem de sagas: verificar via `GET /orders` em vez de `GET /sagas`

### `setup-infra.sh` / `init-sqs.sh`

- Adicionar comentário explicando que filas são criadas automaticamente pelo MassTransit
- Manter o script como referência didática, mas marcar como "não mais necessário"
- Ou remover completamente (a ser decidido)

## Novos Scripts (opcionais)

### `check-health.sh`
Script simples que verifica saúde dos 4 serviços:
```bash
curl -sf http://localhost:5001/health && echo "[OrderService] OK"
curl -sf http://localhost:5002/health && echo "[PaymentService] OK"
curl -sf http://localhost:5003/health && echo "[InventoryService] OK"
curl -sf http://localhost:5004/health && echo "[ShippingService] OK"
```

## Critérios de Aceite

1. `./scripts/happy-path-demo.sh` executa sem erros e demonstra fluxo completo
2. `./scripts/concurrent-saga-demo.sh` executa com `--mode pessimistic` e `--mode optimistic`
3. Nenhum script referencia porta `5000` (SagaOrchestrator removido)
4. Scripts funcionam após `docker compose up` sem passos manuais adicionais
