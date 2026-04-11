# Tasks: mt-demo-scripts

**Feature:** mt-demo-scripts
**Milestone:** M10 - Migração MassTransit

## Resumo

3 tarefas. Depende de `mt-program-config` (arquitetura funcionando para testar os scripts). T1 e T2 são independentes entre si.

---

## T1 — Atualizar happy-path-demo.sh

**Arquivo:** `scripts/happy-path-demo.sh`

**O que fazer:**

1. Remover verificação de health do SagaOrchestrator (porta 5000)
2. Atualizar função de polling de status:
   - `GET http://localhost:5001/orders/{id}` → campo `status`
   - Aguardar `Completed` ou `Failed`
3. Remover chamadas para `GET /sagas/{id}`
4. Atualizar comentários explicativos: mencionar MassTransit, state machine declarativa
5. Verificar que os 4 cenários (happy, payment fail, inventory fail, shipping fail) estão cobertos

**Verificação:** `./scripts/happy-path-demo.sh` executa do início ao fim sem erro em ambiente com `docker compose up`.

---

## T2 — Atualizar concurrent-saga-demo.sh

**Arquivo:** `scripts/concurrent-saga-demo.sh`

**O que fazer:**

1. Remover healthcheck do SagaOrchestrator
2. Atualizar polling para `GET /orders/{id}` (status field)
3. Verificar que flag `--mode` ou equivalente ainda funciona para controlar `INVENTORY_LOCKING_MODE`
4. Atualizar output/comentários para mencionar nova arquitetura

**Verificação:** `./scripts/concurrent-saga-demo.sh --mode pessimistic` executa com 5 pedidos simultâneos e resultado correto (2 Completed, 3 Failed com estoque=2).

---

## T3 — Limpar init-sqs.sh e criar check-health.sh

**O que fazer:**

1. Em `scripts/init-sqs.sh` (se existir): adicionar comentário no topo explicando que não é mais necessário com MassTransit. Deixar o conteúdo como referência didática.

2. Criar `scripts/check-health.sh`:
   ```bash
   #!/bin/bash
   # Verifica saúde dos 4 serviços
   services=("5001:OrderService" "5002:PaymentService" "5003:InventoryService" "5004:ShippingService")
   for svc in "${services[@]}"; do
     port=${svc%%:*}
     name=${svc##*:}
     if curl -sf "http://localhost:$port/health" > /dev/null 2>&1; then
       echo "[$name] OK"
     else
       echo "[$name] FALHOU"
     fi
   done
   ```

**Verificação:** `./scripts/check-health.sh` mostra "OK" para todos os serviços após `docker compose up`.

---

## Dependências

```
mt-program-config (arquitetura funcionando) → T1, T2, T3
T1 e T2 são independentes entre si
T3 independente de T1 e T2
```
