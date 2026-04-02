# Spec: optimistic-locking

## Objetivo

Adicionar locking otimista como alternativa ao pessimistic locking (M5) no `InventoryService`.
Objetivo didático: demonstrar diferença de comportamento, throughput e complexidade entre as duas
estratégias sob concorrência real.

---

## Requisitos

### R1 — Coluna `version` na tabela `inventory`

A tabela `inventory` deve ter uma coluna `version INTEGER NOT NULL DEFAULT 0`.
Essa coluna é incrementada em +1 a cada UPDATE bem-sucedido, funcionando como token de controle de versão.

**Critério de aceite:** `SELECT version FROM inventory WHERE product_id = 'PROD-001'` retorna `0`
após reset; após reserva bem-sucedida, retorna `1`.

---

### R2 — Método `TryReserveOptimisticAsync` no `InventoryRepository`

Implementar método com a seguinte lógica:

1. `SELECT quantity, version FROM inventory WHERE product_id = @id` (sem FOR UPDATE)
2. Verificar se `quantity >= requested` — se não, retornar `(false, "Estoque insuficiente")`
3. `UPDATE inventory SET quantity = quantity - @qty, version = version + 1 WHERE product_id = @id AND version = @expectedVersion`
4. Se `rowsAffected == 0`: conflito detectado → fazer retry (até `maxRetries` tentativas)
5. Se retries esgotam: retornar `(false, "Conflito de versão após N tentativas")`
6. Se UPDATE bem-sucedido: inserir em `inventory_reservations` e retornar `(true, null)`

**Critério de aceite:** Sob 5 requisições concorrentes com estoque=2, exatamente 2 succedem
e 3 falham (com ou sem retry) — sem overbooking.

---

### R3 — Env var `INVENTORY_LOCKING_MODE` com três valores

| Valor          | Comportamento                                                    |
|----------------|------------------------------------------------------------------|
| `pessimistic`  | SELECT FOR UPDATE (padrão atual do M5)                           |
| `optimistic`   | SELECT sem lock + UPDATE com version check + retry automático    |
| `none`         | SELECT sem lock + delay 150ms (demonstra race condition / TOCTOU)|

**R3.1:** `INVENTORY_LOCKING_MODE=pessimistic` deve ser equivalente ao comportamento atual
de `INVENTORY_LOCKING_ENABLED=true`.

**R3.2:** `INVENTORY_LOCKING_MODE=none` deve ser equivalente ao comportamento atual de
`INVENTORY_LOCKING_ENABLED=false`.

**R3.3:** A env var `INVENTORY_LOCKING_ENABLED` é **depreciada** mas mantida por compatibilidade
como fallback (se `INVENTORY_LOCKING_MODE` não estiver definida, usar `INVENTORY_LOCKING_ENABLED`).

**Critério de aceite:** Alterar `INVENTORY_LOCKING_MODE` no `docker-compose.yml` e reiniciar
o serviço muda o comportamento observado nos logs sem recompilação.

---

### R4 — Configuração de retries para modo otimista

Env var `INVENTORY_OPTIMISTIC_MAX_RETRIES` controla quantas tentativas adicionais são feitas
após o primeiro conflito de versão. Default: `3` (total de até 4 tentativas: 1 original + 3 retries).

**R4.1:** Cada tentativa relê `quantity` e `version` do banco antes de tentar o UPDATE.

**R4.2:** Não há delay entre tentativas (o conflito já é sinal de que outro processo terminou —
releitura imediata é o comportamento correto).

**Critério de aceite:** Log deve mostrar `[Inventory][Otimista] Conflito versao, tentativa X/N`
para cada retry antes de sucesso ou falha definitiva.

---

### R5 — Atualização do `Worker.cs`

O Worker deve ler `INVENTORY_LOCKING_MODE` e despachar para o método correto:
- `pessimistic` → `TryReserveAsync(..., useLock: true)`
- `optimistic` → `TryReserveOptimisticAsync(...)`
- `none` → `TryReserveAsync(..., useLock: false)`

**Critério de aceite:** Logs do Worker mostram o modo ativo no startup
(`locking=pessimista|otimista|sem lock`).

---

### R6 — Atualização do `docker-compose.yml`

- Substituir `INVENTORY_LOCKING_ENABLED=true` por `INVENTORY_LOCKING_MODE=pessimistic`
- Adicionar `INVENTORY_OPTIMISTIC_MAX_RETRIES=3` comentado (como documentação do default)

---

### R7 — Documentação em `docs/07-concorrencia-sagas.md`

Adicionar seção "Locking Otimista" cobrindo:
- Como funciona o controle de versão (diagrama ou sequência textual)
- Diferença comportamental vs pessimista (throughput, latência, falhas)
- Quando usar cada estratégia
- Instrução para testar com `concurrent-saga-demo.sh --mode optimistic`

---

## Comportamentos esperados por modo

### `pessimistic`
- PostgreSQL serializa via `FOR UPDATE`
- Concorrentes bloqueiam e aguardam o lock
- Nenhum overbooking possível
- Throughput limitado pela serialização
- Log: `SELECT FOR UPDATE`

### `optimistic`
- Leitura sem lock, UPDATE com `WHERE version = @expected`
- Conflito detectado quando `rowsAffected == 0`
- Retry automático com releitura
- Sem overbooking sob retries suficientes
- Log: `[Otimista] Conflito versao, tentativa X/N` para cada conflito

### `none`
- Leitura sem lock + delay 150ms (janela TOCTOU artificial)
- Múltiplos processos leem o mesmo estoque antes de qualquer UPDATE
- Overbooking ocorre: estoque pode ficar negativo
- Log: `SELECT (sem lock)`

---

## Tratamento de conflito esgotado (R2.6)

Quando os retries se esgotam no modo `optimistic`:
- Retornar `(false, "Conflito de versão: N tentativas sem sucesso")` onde N = maxRetries + 1
- O Worker trata como falha normal → envia `InventoryReply { Success = false }`
- O orquestrador inicia compensação normalmente
- **Não lançar exceção** — falha por contenção é um resultado esperado, não um erro

---

## Fora de escopo

- Locking otimista no `ReleaseAsync` (compensação não precisa — usa reservationId único)
- Locking otimista em outros serviços (Payment, Shipping)
- Métricas de conflitos (seria M6+)
- Backoff exponencial entre retries
