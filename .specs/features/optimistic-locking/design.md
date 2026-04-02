# Design: optimistic-locking

## Decisões de Design

---

### D1 — Migração da coluna `version`

**Opções consideradas:**

| Opção | Prós | Contras |
|-------|------|---------|
| A. `ALTER TABLE` no `EnsureTablesAsync` | Simples, sem scripts extras | Falha se coluna já existe sem `IF NOT EXISTS` |
| B. Recriar tabela | Sempre consistente | Destrói dados, não aceito em produção |
| C. Script SQL separado | Separação de concerns | Mais arquivos, complexidade desnecessária |

**Decisão: Opção A** — usar `ALTER TABLE ... ADD COLUMN IF NOT EXISTS version INTEGER NOT NULL DEFAULT 0`.

`IF NOT EXISTS` torna a operação idempotente: funciona na primeira vez e é no-op nas subsequentes.
`EnsureTablesAsync` já é o ponto centralizado de schema DDL no projeto — manter consistência.

**Impacto no `ResetStockAsync`:** Ao resetar, também zerar `version = 0` para que a demo
comece sempre em estado limpo e previsível.

---

### D2 — Compatibilidade com `INVENTORY_LOCKING_ENABLED`

**Decisão: Deprecar, manter como fallback.**

- Se `INVENTORY_LOCKING_MODE` estiver definida → usa ela (prioridade)
- Se não estiver → lê `INVENTORY_LOCKING_ENABLED` como antes:
  - `true` → `pessimistic`
  - `false` → `none`
- Default final (nenhuma env var) → `pessimistic`

Isso garante que ambientes existentes com `INVENTORY_LOCKING_ENABLED` continuem funcionando
sem alteração. O `docker-compose.yml` será atualizado para usar a nova env var.

**Lógica de parsing no Worker:**

```csharp
var lockingMode = configuration.GetValue<string>("INVENTORY_LOCKING_MODE");
if (string.IsNullOrEmpty(lockingMode))
{
    var legacyEnabled = configuration.GetValue<bool>("INVENTORY_LOCKING_ENABLED", true);
    lockingMode = legacyEnabled ? "pessimistic" : "none";
}
_lockingMode = lockingMode.ToLowerInvariant();
```

---

### D3 — Localização da lógica de retry

**Opções consideradas:**

| Opção | Prós | Contras |
|-------|------|---------|
| A. Loop de retry no `InventoryRepository` | Encapsulamento — Worker não sabe de conflitos | Repositório com lógica de negócio |
| B. Loop de retry no `Worker` | Worker controla política de retry | Vaza detalhes de implementação do repositório |

**Decisão: Opção A** — retry dentro do `TryReserveOptimisticAsync`.

Razão: O Worker já tem sua camada de idempotência e não deveria precisar saber que o modo
otimista usa retry internamente. O retorno `(bool Success, string? ErrorMessage)` é suficiente
para o Worker tomar sua decisão de sucesso/falha.

O número máximo de tentativas é configurado via `INVENTORY_OPTIMISTIC_MAX_RETRIES` (default: 3),
lido pelo repositório via `IConfiguration`.

---

### D4 — O que retornar quando retries esgotam

**Decisão: falha normal com mensagem descritiva.**

```csharp
return (false, $"Conflito de versão: {maxRetries + 1} tentativas sem sucesso");
```

- **Não lançar exceção:** contenção de versão é um resultado esperado, não excepcional
- **Worker trata como falha normal:** envia `InventoryReply { Success = false }`
- **Orquestrador inicia compensação normalmente:** fluxo de compensação já existe e está testado
- **Log de warning** (não error) para cada conflito: `[Otimista] Conflito versao, tentativa X/N`

---

### D5 — Estrutura do método `TryReserveOptimisticAsync`

```
TryReserveOptimisticAsync(productId, quantity, reservationId, sagaId, maxRetries, ct)
│
├── Para tentativa = 1 até maxRetries+1:
│   ├── Abrir conexão + transação
│   ├── SELECT quantity, version FROM inventory WHERE product_id = @id
│   ├── Se produto não encontrado → return (false, "Produto não encontrado")
│   ├── Se quantity < requested → return (false, "Estoque insuficiente")
│   ├── UPDATE ... WHERE product_id = @id AND version = @expectedVersion
│   ├── Se rowsAffected == 0:
│   │   ├── Rollback
│   │   ├── Log warning (tentativa X/N)
│   │   └── Continuar loop (próxima tentativa)
│   └── Se rowsAffected == 1:
│       ├── INSERT INTO inventory_reservations
│       ├── Commit
│       └── return (true, null)
│
└── Retries esgotados → return (false, "Conflito de versão: N tentativas sem sucesso")
```

**Nota importante:** Cada tentativa abre uma **nova conexão e transação**. Não há como reusar
a transação após conflito. O loop recomeça do SELECT.

---

### D6 — Impacto no `ResetStockAsync`

Ao resetar o estoque, também resetar `version = 0`:

```sql
UPDATE inventory SET quantity = @quantity, version = 0 WHERE product_id = @productId;
DELETE FROM inventory_reservations WHERE product_id = @productId;
```

Isso garante que a demo de concorrência sempre inicia com `version = 0`, tornando os logs
e o comportamento previsíveis e reproduzíveis.

---

## Componentes Afetados

| Arquivo | Mudança |
|---------|---------|
| `src/InventoryService/InventoryRepository.cs` | `EnsureTablesAsync` + `TryReserveOptimisticAsync` + `ResetStockAsync` |
| `src/InventoryService/Worker.cs` | Leitura de `INVENTORY_LOCKING_MODE`, fallback, despacho |
| `docker-compose.yml` | Substituir `INVENTORY_LOCKING_ENABLED` por `INVENTORY_LOCKING_MODE` |
| `docs/07-concorrencia-sagas.md` | Nova seção "Locking Otimista" |

---

## Não afetados

- `Shared/` — nenhuma mudança em contracts ou helpers
- `SagaOrchestrator/` — fluxo de compensação já trata `Success=false` corretamente
- Scripts bash — `concurrent-saga-demo.sh` usa `--no-lock` que mapeia para `INVENTORY_LOCKING_ENABLED=false`;
  continua funcionando via compatibilidade. Adicionar `--mode` ao script está fora de escopo desta feature.
