# Design: demo-scripts

## Decisao 1 — Funcoes compartilhadas: `scripts/lib/common.sh`

**Opcoes avaliadas:**
- A) Duplicar codigo nos dois scripts
- B) Extrair para `scripts/lib/common.sh` e usar `source`

**Decisao: B — extrair para `scripts/lib/common.sh`**

**Justificativa:**
- As funcoes `log`, `success`, `warn`, `error`, `header` sao identicas nos dois scripts
- `check_health` e `poll_saga_until_terminal` serao usadas por ambos
- Duplicar 80+ linhas de boilerplate dificulta manutencao e e confuso para leitura didatica
- `source "$(dirname "$0")/lib/common.sh"` funciona com `bash scripts/X.sh` a partir da raiz

**Funcoes extraidas para `common.sh`:**
```
ORDER_URL, INVENTORY_URL, ORCHESTRATOR_URL (defaults)
Cores e aliases: RED, GREEN, YELLOW, CYAN, BOLD, NC
log(), success(), warn(), error(), header()
check_health(name, url)         → verifica /health, aborta se down
poll_saga(saga_id, timeout)     → retorna estado terminal ou "Timeout"
get_stock(product_id)           → retorna quantidade atual
```

---

## Decisao 2 — Verificacao de transicoes

**Abordagem escolhida:** extração do array de destinos `to` com `jq`, comparação por `grep -q`.

**Implementacao:**

```bash
# Obter historico de transicoes como lista de estados "to"
TRANSITIONS=$(curl -sf "$ORCHESTRATOR_URL/sagas/$SAGA_ID" | jq -r '[.transitions[].to] | join(" ")')
# Verificar presenca
echo "$TRANSITIONS" | grep -q "PaymentRefunding"
# Verificar sequencia exata (cenario 1)
[[ "$TRANSITIONS" == "PaymentProcessing InventoryReserving ShippingScheduling Completed" ]]
```

**Por que nao comparacao exata de array JSON:** mais fragil a alteracoes de ordem de campos JSON e mais verbosa com jq.

---

## Decisao 3 — Deteccao do modo lock no `--no-lock`

**Problema:** a flag `--no-lock` nao pode reconfigurar o servico em execucao. O usuario precisa reiniciar o container manualmente.

**Abordagem:** ao usar `--no-lock`, o script verifica o log do container:

```bash
docker logs saga-inventory-service 2>&1 | grep -q "locking=SEM LOCK"
```

Se a linha nao for encontrada, o script exibe instrucao precisa e aborta:

```
✗ Para o modo sem lock, reinicie o inventory-service com:
    INVENTORY_LOCKING_ENABLED=false docker compose up -d inventory-service
  Verifique no log: "locking=SEM LOCK (demonstracao de race condition)"
  Entao reexecute este script.
```

**Dependencia:** requer `docker` instalado. O script verifica `command -v docker` antes.

---

## Decisao 4 — Estrategia de polling

| Parametro | happy-path-demo | concurrent-saga-demo |
|-----------|----------------|---------------------|
| Intervalo | 2s | 2s |
| Timeout por saga | 60s | 90s (mais sagas, mais lento) |
| Estrategia | sequencial (uma saga por cenario) | loop sobre todas as sagas ate todas terminais |

---

## Estrutura de arquivos resultante

```
scripts/
├── lib/
│   └── common.sh              # Funcoes compartilhadas
├── happy-path-demo.sh          # Novo: 4 cenarios sequenciais
└── concurrent-saga-demo.sh     # Reescrito: unico loop, deteccao de lock
```
