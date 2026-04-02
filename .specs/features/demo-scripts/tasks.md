# Tasks: demo-scripts

## T1 — Criar `scripts/lib/common.sh`

**Status:** completed

**Arquivo:** `scripts/lib/common.sh`

**Conteudo:**
- Defaults de URL (ORDER_URL, INVENTORY_URL, ORCHESTRATOR_URL)
- Definicoes de cor e funcoes de log (log, success, warn, error, header)
- `check_health(name, url)` — curl /health, aborta se down
- `poll_saga(saga_id, timeout)` — poll a cada 2s, retorna estado terminal ou "Timeout"
- `get_stock(product_id)` — curl /inventory/stock/{id}, retorna quantidade

**Verificacao:** `bash -n scripts/lib/common.sh` sem erros de sintaxe

---

## T2 — Reescrever `scripts/concurrent-saga-demo.sh`

**Status:** completed

**Depende de:** T1

**Correcoes obrigatorias:**
- Bug 1: Remover loops duplos, implementar unico loop com arquivos temporarios
- Bug 2: `--no-lock` verifica `docker logs saga-inventory-service` e aborta com instrucao se incorreto
- Bug 3: Remover `RAW_RESPONSES` e loops sem captura

**Fluxo correto:**
1. Verificar dependencias (curl, jq, docker se --no-lock)
2. Verificar saude dos 3 servicos via `check_health`
3. Se `--no-lock`: verificar log do container, abortar se nao configurado
4. Resetar estoque (uma vez)
5. Disparar N pedidos em paralelo → subshells → arquivos temp
6. `wait` por todos
7. Coletar IDs dos arquivos temp
8. Poll todas as sagas via `poll_saga` ate terminal
9. Exibir resultado + diagnostico
10. `rm -rf "$TMP_DIR"`

**Verificacao:** `bash -n scripts/concurrent-saga-demo.sh` sem erros

---

## T3 — Criar `scripts/happy-path-demo.sh` — Cenario 1: Happy Path

**Status:** completed

**Depende de:** T1

**Conteudo:**
- `source "$(dirname "$0")/lib/common.sh"`
- Verificar dependencias e saude dos servicos
- Resetar estoque para 2 unidades
- POST /orders (sem headers de falha)
- Poll ate Completed via `poll_saga`
- Verificar sequencia de transicoes: `PaymentProcessing InventoryReserving ShippingScheduling Completed`
- Verificar estoque decrementou em 1

**Verificacao:** `bash -n scripts/happy-path-demo.sh` sem erros; logica de verificacao correta

---

## T4 — Adicionar Cenario 2: Falha no Pagamento

**Status:** completed

**Depende de:** T3

**Conteudo adicional em `happy-path-demo.sh`:**
- Capturar estoque atual antes do pedido
- POST /orders com `-H "X-Simulate-Failure: payment"`
- Poll ate Failed
- Verificar `PaymentProcessing` presente nas transicoes
- Verificar `InventoryReserving` ausente
- Verificar estoque inalterado (igual ao capturado antes)

---

## T5 — Adicionar Cenario 3: Falha no Inventario

**Status:** completed

**Depende de:** T4

**Conteudo adicional em `happy-path-demo.sh`:**
- POST /orders com `-H "X-Simulate-Failure: inventory"`
- Poll ate Failed
- Verificar `PaymentRefunding` presente
- Verificar `ShippingScheduling` ausente

---

## T6 — Adicionar Cenario 4: Falha no Shipping

**Status:** completed

**Depende de:** T5

**Conteudo adicional em `happy-path-demo.sh`:**
- POST /orders com `-H "X-Simulate-Failure: shipping"`
- Poll ate Failed
- Verificar `InventoryReleasing` presente
- Verificar `PaymentRefunding` presente

---

## T7 — Atualizar `.specs/prompts/README.md`

**Status:** completed

**Depende de:** T2, T6

**Alteracao:** marcar `demo-scripts` como DONE na tabela de sequencia; atualizar linha de entregaveis

---

## T8 — Atualizar STATE.md, ROADMAP.md e memory

**Status:** completed

**Depende de:** T7

**Alteracoes:**
- `STATE.md`: adicionar decisao sobre demo-scripts
- `ROADMAP.md`: marcar `demo-scripts` como DONE em M5 (nova feature adicionada ao milestone)
- `memory/project_state.md`: atualizar estado atual
