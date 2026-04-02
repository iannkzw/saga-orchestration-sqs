# Spec: demo-scripts

## Visao Geral

Corrigir o script de concorrencia existente (3 bugs criticos) e criar um script de
demonstracao do happy path com 4 cenarios de falha. Ambos os scripts devem ser
independentes, corretos e didaticamente claros.

---

## Requisitos

### R1 — Reescrever `scripts/concurrent-saga-demo.sh`

**Entradas:**
- `--no-lock` (flag opcional): ativa modo sem lock
- `--pedidos N` (opcional, default 5): numero de pedidos simultaneos
- `--estoque N` (opcional, default 2): estoque inicial do PROD-001

**Fluxo correto (unico loop de disparo):**
1. Verificar saude dos servicos (OrderService, InventoryService, SagaOrchestrator)
2. Resetar estoque para N unidades — **uma unica vez**
3. Disparar M pedidos em paralelo usando subshells com arquivos temporarios
4. Aguardar todos os subshells (`wait`)
5. Coletar IDs dos arquivos temporarios
6. Poll de todas as sagas ate estado terminal (`Completed` ou `Failed`)
7. Exibir resultado e diagnostico
8. Limpar arquivos temporarios

**Criterios de aceite (COM lock):**
- Exatamente `ESTOQUE_INICIAL` sagas em `Completed` e `NUM_PEDIDOS - ESTOQUE_INICIAL` em `Failed`
- Estoque final = 0
- Nenhum pedido disparado duas vezes

**Criterios de aceite (SEM lock):**
- Script aborta se `INVENTORY_LOCKING_ENABLED=false` nao estiver configurado no servico
- Mensagem de instrucao precisa indicando como reconfigurar

**Bugs corrigidos:**
- Bug 1: Dois loops de disparo → unico loop com arquivos temporarios
- Bug 2: `--no-lock` detecta configuracao real do servico e aborta se incorreta
- Bug 3: `RAW_RESPONSES` removido (era dead code)

---

### R2 — Criar `scripts/happy-path-demo.sh`

**Entradas:** nenhuma (script autocontido)

**Cenarios em sequencia:**

#### R2.1 — Cenario 1: Happy Path Completo

- POST `/orders` com PROD-001, quantidade 1
- Poll ate `Completed`
- Verificar sequencia de transicoes: `Pending → PaymentProcessing → InventoryReserving → ShippingScheduling → Completed`
- Verificar estoque decrementou em 1

**Criterio de aceite:** saga em `Completed` com todas as 4 transicoes presentes na ordem correta.

#### R2.2 — Cenario 2: Falha no Pagamento

- POST `/orders` com header `X-Simulate-Failure: payment`
- Poll ate `Failed`
- Verificar transicoes contem `PaymentProcessing`
- Verificar transicoes **nao contem** `InventoryReserving` (pagamento falhou antes)
- Verificar estoque **nao foi alterado**

**Criterio de aceite:** saga em `Failed`, sem `InventoryReserving` no historico.

#### R2.3 — Cenario 3: Falha no Inventario

- POST `/orders` com header `X-Simulate-Failure: inventory`
- Poll ate `Failed`
- Verificar transicoes contem `PaymentRefunding` (compensacao do pagamento)
- Verificar transicoes **nao contem** `ShippingScheduling`

**Criterio de aceite:** saga em `Failed` com `PaymentRefunding` presente.

#### R2.4 — Cenario 4: Falha no Shipping

- POST `/orders` com header `X-Simulate-Failure: shipping`
- Poll ate `Failed`
- Verificar transicoes contem `InventoryReleasing` e `PaymentRefunding` (cascata completa)

**Criterio de aceite:** saga em `Failed` com ambas as compensacoes presentes.

---

## Definicao de "verificar historico de transicoes"

O endpoint `GET http://localhost:5002/sagas/{sagaId}` retorna:

```json
{
  "state": "Completed",
  "transitions": [
    {"from": "Pending",             "to": "PaymentProcessing",  "triggeredBy": "..."},
    {"from": "PaymentProcessing",   "to": "InventoryReserving", "triggeredBy": "..."},
    {"from": "InventoryReserving",  "to": "ShippingScheduling", "triggeredBy": "..."},
    {"from": "ShippingScheduling",  "to": "Completed",          "triggeredBy": "..."}
  ]
}
```

**Verificacao de presenca:** extrair campo `to` de cada transicao com `jq '[.transitions[].to]'`
e checar se o estado esperado esta presente com `grep -q`.

**Verificacao de sequencia (cenario 1):** extrair array de `to` e comparar com string
esperada `PaymentProcessing InventoryReserving ShippingScheduling Completed`.

**Verificacao de ausencia:** checar que o estado **nao** aparece em nenhum campo `to`.

---

## Output esperado por cenario

```
=== Cenario 1: Happy Path ===
[HH:MM:SS] ✓ Pedido criado: orderId=... sagaId=...
[HH:MM:SS] ✓ Saga atingiu Completed em Xs
[HH:MM:SS] ✓ Transicoes: Pending → PaymentProcessing → InventoryReserving → ShippingScheduling → Completed
[HH:MM:SS] ✓ Estoque de PROD-001: N-1 (era N)

=== Cenario 2: Falha no Pagamento ===
[HH:MM:SS] ✓ Pedido criado: orderId=... sagaId=...
[HH:MM:SS] ✓ Saga atingiu Failed em Xs
[HH:MM:SS] ✓ PaymentProcessing presente (pagamento tentado)
[HH:MM:SS] ✓ InventoryReserving ausente (nao chegou ao inventario)
[HH:MM:SS] ✓ Estoque nao alterado: N-1 (esperado)

=== Cenario 3: Falha no Inventario ===
[HH:MM:SS] ✓ Pedido criado: orderId=... sagaId=...
[HH:MM:SS] ✓ Saga atingiu Failed em Xs
[HH:MM:SS] ✓ PaymentRefunding presente (compensacao executada)
[HH:MM:SS] ✓ ShippingScheduling ausente (nao chegou ao shipping)

=== Cenario 4: Falha no Shipping ===
[HH:MM:SS] ✓ Pedido criado: orderId=... sagaId=...
[HH:MM:SS] ✓ Saga atingiu Failed em Xs
[HH:MM:SS] ✓ InventoryReleasing presente (compensacao de inventario)
[HH:MM:SS] ✓ PaymentRefunding presente (compensacao de pagamento)
```

---

## Endpoints utilizados

| Metodo | URL | Descricao |
|--------|-----|-----------|
| POST | `http://localhost:5001/orders` | Criar pedido |
| GET | `http://localhost:5002/sagas/{sagaId}` | Estado e transicoes da saga |
| GET | `http://localhost:5004/inventory/stock/PROD-001` | Quantidade em estoque |
| POST | `http://localhost:5004/inventory/reset` | Resetar estoque |
| GET | `http://localhost:5001/health` | Health check OrderService |
| GET | `http://localhost:5002/health` | Health check SagaOrchestrator |
| GET | `http://localhost:5004/health` | Health check InventoryService |

---

## Estados terminais

`Completed`, `Failed`

## Estados de compensacao no historico

`PaymentRefunding`, `InventoryReleasing`, `ShippingCancelling`

## Dependencias de ferramentas

`curl`, `jq` — verificados no inicio de cada script.
