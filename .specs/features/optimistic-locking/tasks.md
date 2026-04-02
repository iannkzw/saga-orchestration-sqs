# Tasks: optimistic-locking

## Resumo

6 tarefas atômicas, sem dependências paralelas — executar em ordem (T2 depende de T1).

---

## T1 — Migração do schema: ADD COLUMN version

**Arquivo:** `src/InventoryService/InventoryRepository.cs`

**O que fazer:**

1. Em `EnsureTablesAsync`, adicionar após o `CREATE TABLE IF NOT EXISTS inventory (...)`:
   ```sql
   ALTER TABLE inventory ADD COLUMN IF NOT EXISTS version INTEGER NOT NULL DEFAULT 0;
   ```

2. Em `ResetStockAsync`, adicionar `version = 0` ao UPDATE:
   ```sql
   UPDATE inventory SET quantity = @quantity, version = 0 WHERE product_id = @productId;
   ```

**Verificação:** Após `docker compose up`, executar:
```bash
docker exec -it saga-postgres psql -U saga saga_db -c "\d inventory"
# Deve mostrar coluna version INTEGER NOT NULL DEFAULT 0

curl -s localhost:5003/inventory/reset -X POST -H "Content-Type: application/json" -d '{"productId":"PROD-001","quantity":5}'
docker exec -it saga-postgres psql -U saga saga_db -c "SELECT product_id, quantity, version FROM inventory"
# Deve mostrar: PROD-001 | 5 | 0
```

---

## T2 — Implementar `TryReserveOptimisticAsync`

**Arquivo:** `src/InventoryService/InventoryRepository.cs`

**O que fazer:**

Adicionar método após `TryReserveAsync`. Assinatura:
```csharp
public async Task<(bool Success, string? ErrorMessage)> TryReserveOptimisticAsync(
    string productId,
    int quantity,
    string reservationId,
    Guid sagaId,
    int maxRetries = 3,
    CancellationToken ct = default)
```

Lógica (ver design.md D5):
- Loop de 1 até `maxRetries + 1`
- Cada iteração: nova conexão + transação
- SELECT sem lock com quantity e version
- UPDATE com WHERE version = @expectedVersion
- Se rowsAffected == 0 → rollback + log + continuar loop
- Se rowsAffected == 1 → INSERT reservations + commit + return (true, null)
- Após loop esgotado → return (false, "Conflito de versão: N tentativas sem sucesso")

**Verificação:**
```bash
# Modo otimista com estoque=2, 5 pedidos paralelos
curl -s localhost:5003/inventory/reset -X POST -H "Content-Type: application/json" -d '{"productId":"PROD-001","quantity":2}'
# Disparar 5 pedidos via concurrent-saga-demo.sh (ou curl manual)
# Verificar logs: exatamente 2 SUCCESS, 3 FAILED, sem estoque negativo
docker exec -it saga-postgres psql -U saga saga_db -c "SELECT quantity FROM inventory WHERE product_id='PROD-001'"
# quantity deve ser 0 (não negativo)
```

---

## T3 — Refatorar env var: `INVENTORY_LOCKING_MODE`

**Arquivo:** `src/InventoryService/Worker.cs`

**O que fazer:**

1. Substituir campo `_lockingEnabled: bool` por `_lockingMode: string`

2. No construtor, implementar parsing com fallback:
   ```csharp
   var lockingMode = configuration.GetValue<string>("INVENTORY_LOCKING_MODE");
   if (string.IsNullOrEmpty(lockingMode))
   {
       var legacyEnabled = configuration.GetValue<bool>("INVENTORY_LOCKING_ENABLED", true);
       lockingMode = legacyEnabled ? "pessimistic" : "none";
   }
   _lockingMode = lockingMode.ToLowerInvariant();
   ```

3. Injetar `IConfiguration` no construtor (já existe) e ler `INVENTORY_OPTIMISTIC_MAX_RETRIES`:
   ```csharp
   _optimisticMaxRetries = configuration.GetValue<int>("INVENTORY_OPTIMISTIC_MAX_RETRIES", 3);
   ```

4. Atualizar log de startup para mostrar o modo

**Verificação:** Ao reiniciar o serviço, log deve mostrar o modo ativo:
```
InventoryService worker iniciado — locking=pessimista (FOR UPDATE)
# ou
InventoryService worker iniciado — locking=otimista (version check, max retries=3)
# ou
InventoryService worker iniciado — locking=sem lock (demo race condition)
```

---

## T4 — Atualizar despacho no `Worker.cs`

**Arquivo:** `src/InventoryService/Worker.cs`

**O que fazer:**

Em `HandleReserveInventoryAsync`, substituir o bloco que chama `TryReserveAsync` para
despachar conforme `_lockingMode`:

```csharp
(bool ok, string? err) result = _lockingMode switch
{
    "optimistic" => await _inventoryRepository.TryReserveOptimisticAsync(
        firstItem.ProductId, firstItem.Quantity, newReservationId, command.SagaId,
        _optimisticMaxRetries, ct),
    "pessimistic" => await _inventoryRepository.TryReserveAsync(
        firstItem.ProductId, firstItem.Quantity, newReservationId, command.SagaId,
        useLock: true, ct),
    _ => await _inventoryRepository.TryReserveAsync(
        firstItem.ProductId, firstItem.Quantity, newReservationId, command.SagaId,
        useLock: false, ct)
};
```

**Verificação:** Com `INVENTORY_LOCKING_MODE=optimistic`, logs do serviço devem mostrar
`[Otimista]` nos traces de reserva. Com `pessimistic`, mostrar `SELECT FOR UPDATE`.

---

## T5 — Atualizar `docker-compose.yml`

**Arquivo:** `docker-compose.yml`

**O que fazer:**

Na seção `environment` do serviço `inventory-service`:
1. Substituir `INVENTORY_LOCKING_ENABLED=true` por `INVENTORY_LOCKING_MODE=pessimistic`
2. Adicionar linha comentada: `# INVENTORY_OPTIMISTIC_MAX_RETRIES=3  # default: 3`
3. Adicionar comentário explicando os 3 modos disponíveis

**Verificação:** `docker compose config | grep -A5 inventory-service` deve mostrar a nova env var.

---

## T6 — Atualizar `docs/07-concorrencia-sagas.md`

**Arquivo:** `docs/07-concorrencia-sagas.md`

**O que fazer:**

Adicionar seção nova "## Locking Otimista" após a seção de pessimistic locking existente, cobrindo:

1. **Como funciona:** fluxo SELECT → verificação → UPDATE com version → retry
2. **Quando há conflito:** o que acontece, log esperado, retry automático
3. **Comparativo:** tabela pessimistic vs otimistic (throughput, latência, falhas, complexidade)
4. **Quando usar cada um**
5. **Como testar:** alterar `INVENTORY_LOCKING_MODE=optimistic` no docker-compose + reiniciar serviço

---

## Dependências

```
T1 (schema) → T2 (método usa version) → T3+T4 (Worker usa método)
T5 independente de T1-T4
T6 independente de T1-T5
```

Ordem de execução: T1 → T2 → T3 → T4 → T5 → T6
