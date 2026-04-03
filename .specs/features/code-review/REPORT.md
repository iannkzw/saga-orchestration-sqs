# Relatório de Code Review — Milestones M1–M5

**Data:** 2026-04-03
**Escopo:** InventoryService, SagaOrchestrator, Shared, Scripts bash, docker-compose
**Total de findings:** 16 (0 CRITICAL · 2 HIGH · 6 MEDIUM · 4 LOW · 4 INFO)

---

## Sumário por Severidade

| Severidade | Qtd | Arquivos afetados |
|------------|-----|-------------------|
| CRITICAL   | 0   | —                 |
| HIGH       | 2   | Scripts, SagaOrchestrator |
| MEDIUM     | 6   | InventoryRepository, SagaOrchestrator Worker, InventoryService Worker, Scripts |
| LOW        | 4   | IdempotencyStore, SagaOrchestrator, happy-path-demo |
| INFO       | 4   | InventoryService, SagaOrchestrator, docker-compose |

---

## HIGH

---

### [HIGH-01] grep case-sensitive quebra verificação do modo `--no-lock`

**Arquivo:** `scripts/concurrent-saga-demo.sh:64`

**Descrição:**
O script verifica se o container está em modo sem lock com:
```bash
docker logs saga-inventory-service 2>&1 | grep -q "locking=SEM LOCK"
```
Mas o log real emitido pelo Worker.cs (linha ~57) é em caixa baixa:
```
InventoryService worker iniciado — locking=sem lock (demonstracao de race condition)
```
`grep` é case-sensitive por padrão. O padrão `"locking=SEM LOCK"` nunca vai corresponder a `"locking=sem lock"`. Como resultado, **o script always falha ao verificar o modo no-lock**, mesmo quando o serviço está corretamente configurado, abortando com a mensagem de erro sobre mode incorreto e tornando o `--no-lock` inutilizável.

**Sugestão:** Usar `grep -qi "locking=sem lock"` (flag `-i` para case-insensitive) ou ajustar o padrão para corresponder exatamente à string emitida: `grep -q "locking=sem lock (demonstracao"`.

---

### [HIGH-02] Dual-write não-atômico: estado salvo, comando não enviado

**Arquivo:** `src/SagaOrchestrator/Worker.cs:138` (`HandleSuccessAsync`)

**Descrição:**
O fluxo de `HandleSuccessAsync` e `HandleFailureAsync` segue este padrão:
1. `await db.SaveChangesAsync(ct)` — persiste a nova transição de estado
2. `await SendCommandToQueueAsync(...)` — envia o próximo comando para SQS

Se a operação (2) falhar (ex: timeout LocalStack, SQS indisponível), a mensagem de reply que disparou o handler **não é deletada** (pois `DeleteMessageAsync` está após o retorno do handler). Quando a mensagem for re-polled, `saga.CurrentState` já avançou para o próximo estado (ex: `InventoryReserving`), mas o handler recebe um reply do tipo anterior (ex: `PaymentReplies`). A máquina de estados avança novamente com `TryAdvance(InventoryReserving)` → `ShippingScheduling`, pulando o passo de inventário inteiro. A saga completa com `Completed` sem que o inventário tenha sido reservado.

Este é o problema clássico de "dual-write sem saga outbox". Em produção causaria corrupção de estado. No PoC, o risco é baixo dado ambiente controlado, mas o comportamento está incorreto em cenários de falha parcial.

**Sugestão:** Usar padrão Transactional Outbox: salvar o comando a enviar na mesma transação do banco e publicar via job separado. Alternativamente, como workaround simples para PoC: inverter a ordem — enviar o comando SQS primeiro, depois salvar no DB (aceitar que em falha do DB o comando já foi enviado, idempotency no serviço destino absorve o reenvio).

---

## MEDIUM

---

### [MEDIUM-01] `ResetStockAsync` executa dois comandos sem transação explícita

**Arquivo:** `src/InventoryService/InventoryRepository.cs:582`

**Descrição:**
```csharp
cmd.CommandText = """
    INSERT INTO inventory ... ON CONFLICT DO UPDATE SET quantity = @quantity, version = 0;
    DELETE FROM inventory_reservations WHERE product_id = @productId;
    """;
await cmd.ExecuteNonQueryAsync();
```
Dois comandos SQL são executados em um único `CommandText` sem abertura explícita de transação via `BeginTransactionAsync`. Em PostgreSQL, cada statement sem transação explícita é auto-committed. Se ocorrer falha de rede ou crash entre os dois statements, o estoque pode ser resetado mas as reservas ainda existirem na tabela `inventory_reservations`. O resultado seria estoque aparentemente disponível mas com reservas "fantasma" que nunca serão liberadas, corrompendo o inventário para execuções seguintes.

**Sugestão:** Envolver os dois comandos em uma transação explícita:
```csharp
await using var tx = await conn.BeginTransactionAsync(ct);
// ... execute ambos os commands com cmd.Transaction = tx ...
await tx.CommitAsync(ct);
```

---

### [MEDIUM-02] `ReleaseAsync` não incrementa `version` — quebra invariante do locking otimista

**Arquivo:** `src/InventoryService/InventoryRepository.cs:490`

**Descrição:**
```csharp
// UPDATE inventory SET quantity = quantity + @qty WHERE product_id = @productId
```
O `ReleaseAsync` (usado para compensação em todos os modos) restaura `quantity` sem incrementar `version`. Isso quebra o invariante do locking otimista: `version` deve mudar a cada modificação de `quantity`. Cenário problemático:
1. `TryReserveOptimisticAsync` lê `version=5, quantity=1`
2. `ReleaseAsync` executa (compensação de outra saga): `quantity=2`, `version` permanece 5
3. A reserva otimista faz `UPDATE WHERE version=5` → sucesso (version ainda é 5)
4. Outra reserva concorrente também leu `version=5` → também tem sucesso

O resultado é overbooking mesmo com locking otimista ativo em cenários que envolvam compensação concorrente.

**Sugestão:** Adicionar `version = version + 1` ao UPDATE do `ReleaseAsync`:
```csharp
"UPDATE inventory SET quantity = quantity + @qty, version = version + 1 WHERE product_id = @productId"
```

---

### [MEDIUM-03] `SendReplyAsync` passa `string.Empty` como `sagaId` no span de telemetria

**Arquivo:** `src/InventoryService/Worker.cs:278`

**Descrição:**
```csharp
using (SagaActivitySource.StartSendReply(typeof(T).Name, string.Empty))
```
O `sagaId` passado para todos os spans de envio de reply é sempre `""`. A tag `saga.id` fica em branco, impossibilitando correlacionar o span `send InventoryReply` à saga correspondente em ferramentas de tracing (Jaeger, Tempo). O `reply` passado para o método tem `SagaId` disponível como propriedade — bastaria extraí-lo via reflexão ou cast.

**Sugestão:** Adicionar `sagaId` como parâmetro ao `SendReplyAsync` e passá-lo para `StartSendReply`:
```csharp
private async Task SendReplyAsync<T>(string repliesQueueUrl, T reply, Guid sagaId, CancellationToken ct)
{
    using (SagaActivitySource.StartSendReply(typeof(T).Name, sagaId.ToString()))
    { ... }
}
```
E nos dois call sites passar `reply.SagaId`.

---

### [MEDIUM-04] `DeserializeItems` silencia exceção sem logar detalhes

**Arquivo:** `src/SagaOrchestrator/Worker.cs:354`

**Descrição:**
```csharp
private static List<InventoryItem> DeserializeItems(string itemsJson)
{
    try { ... }
    catch { return []; }   // exceção silenciada sem log
}
```
Se `ItemsJson` de uma `SagaInstance` estiver corrompido ou em formato incompatível, a exceção é descartada e retornada uma lista vazia. O comando `ReserveInventory` é enviado com `Items = []`, o `InventoryService` retorna `errorMessage = "Nenhum item no pedido"` e a saga entra em compensação desnecessária. A causa raiz (JSON malformado/inválido) fica completamente invisível nos logs.

**Sugestão:** Logar a exceção com nível `LogError` antes de retornar `[]`:
```csharp
catch (Exception ex)
{
    _logger.LogError(ex, "Falha ao desserializar ItemsJson da saga. Json={ItemsJson}", itemsJson);
    return [];
}
```
Alternativa mais robusta: validar `ItemsJson` ao persistir a `SagaInstance`.

---

### [MEDIUM-05] `SendCommandToQueueAsync` chama `GetQueueUrlAsync` sem cache

**Arquivo:** `src/SagaOrchestrator/Worker.cs:309`

**Descrição:**
```csharp
var queueUrlResponse = await _sqs.GetQueueUrlAsync(commandQueue, ct);
```
Esta chamada é feita a cada comando enviado (ReserveInventory, ScheduleShipping, RefundPayment, ReleaseInventory, CancelShipping). O `ExecuteAsync` já resolve e cacheia as URLs das filas de reply no startup, mas o método de envio de comandos faz a resolução de URL por mensagem. No modo concorrente com múltiplas sagas simultâneas, isso multiplica as chamadas ao LocalStack/SQS sem necessidade.

**Sugestão:** Adicionar um dicionário estático ou campo `_commandQueueUrls` lido/populado no startup, análogo ao `queueUrls` já usado para reply queues.

---

### [MEDIUM-06] `declare -A SAGA_STATES` requer Bash 4+ — incompatível com macOS padrão (Bash 3.2)

**Arquivo:** `scripts/concurrent-saga-demo.sh:128`

**Descrição:**
```bash
declare -A SAGA_STATES
```
Arrays associativos (`declare -A`) foram introduzidos no Bash 4. macOS vem com Bash 3.2 por padrão (limitação de licença GPL). O shebang `#!/usr/bin/env bash` usará o Bash do sistema em macOS, causando:
```
concurrent-saga-demo.sh: line 128: declare: -A: invalid option
```
O script abortaria ao tentar criar o array associativo.

**Sugestão:** Documentar no README que `bash >= 4` é necessário (instalável via `brew install bash`). Alterar o shebang para `#!/usr/bin/env bash` com nota de pré-requisito, ou substituir `declare -A SAGA_STATES` por um arquivo temporário de key-value se portabilidade for necessária.

---

## LOW

---

### [LOW-01] `SaveAsync` loga "salva" mesmo quando `ON CONFLICT DO NOTHING` descartou a inserção

**Arquivo:** `src/Shared/Idempotency/IdempotencyStore.cs:72`

**Descrição:**
```csharp
await cmd.ExecuteNonQueryAsync();
_logger.LogInformation("Idempotency save: chave {Key} salva para saga {SagaId}", idempotencyKey, sagaId);
```
O log é emitido incondicionalmente após o `ExecuteNonQueryAsync`, mesmo quando `ON CONFLICT DO NOTHING` descartou silenciosamente a inserção (chave já existia). O valor de retorno de `ExecuteNonQueryAsync()` indica `rowsAffected = 0` nesse caso, mas não é verificado. Durante diagnóstico de re-entrega de mensagens, o log pode induzir a erro: parece que a chave foi "salva" quando na verdade já existia de uma entrega anterior.

**Sugestão:**
```csharp
var rowsAffected = await cmd.ExecuteNonQueryAsync();
if (rowsAffected > 0)
    _logger.LogInformation("Idempotency save: chave {Key} salva para saga {SagaId}", ...);
else
    _logger.LogDebug("Idempotency save: chave {Key} ja existia (ON CONFLICT), ignorado", idempotencyKey);
```

---

### [LOW-02] `ProcessReplyAsync` usa `GetProperty` sem validação — mensagem malformada entra em retry loop

**Arquivo:** `src/SagaOrchestrator/Worker.cs:90`

**Descrição:**
```csharp
var sagaId = baseReply.GetProperty("SagaId").GetGuid();
var success = baseReply.GetProperty("Success").GetBoolean();
```
`JsonElement.GetProperty` lança `KeyNotFoundException` se o campo não existir. Mensagens inválidas (ex: dead-letter redrive de outro sistema, payload com schema errado) causariam a exceção ser capturada pelo catch do loop externo, logada como erro, e a mensagem ficaria na fila para retentar até atingir o `maxReceiveCount` e ir para a DLQ. O fluxo de retry para uma única mensagem malformada bloqueia a thread de polling daquela fila por alguns segundos a cada iteração.

**Sugestão:** Usar `TryGetProperty` para campos obrigatórios e deletar a mensagem (ou movê-la para DLQ manualmente) se inválida:
```csharp
if (!baseReply.TryGetProperty("SagaId", out var sagaIdProp) || ...)
{
    _logger.LogError("Mensagem malformada em {Queue}: {Body}", mapping.QueueName, message.Body);
    await _sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, ct);
    return;
}
```

---

### [LOW-03] `TMP_DIR` sem cleanup via `trap` em caso de falha

**Arquivo:** `scripts/concurrent-saga-demo.sh:106`

**Descrição:**
```bash
TMP_DIR=$(mktemp -d)
```
O diretório temporário é criado mas não há `trap "rm -rf $TMP_DIR" EXIT`. Se o script abortar por `set -e` ou `exit 1` (ex: verificação de dependência), o diretório permanece no sistema. Em execuções repetidas do demo, múltiplos diretórios acumulam (ex: `/tmp/tmp.XXXXXX`).

**Sugestão:** Adicionar logo após a criação do `TMP_DIR`:
```bash
TMP_DIR=$(mktemp -d)
trap 'rm -rf "$TMP_DIR"' EXIT
```

---

### [LOW-04] `happy-path-demo.sh` — Cenário 3 não verifica estoque após compensação

**Arquivo:** `scripts/happy-path-demo.sh:250`

**Descrição:**
No Cenário 3 (falha no inventário), o script verifica:
- Estado final `Failed` ✓
- Transição `PaymentRefunding` presente ✓
- Transição `ShippingScheduling` ausente ✓

Mas não verifica se o estoque permaneceu inalterado após a compensação. A falha de inventário acontece via `SimulateFailure`, então o inventário não é reservado. O estoque deveria permanecer o mesmo de antes do cenário 3. Sem essa assertiva, um bug que inadvertidamente reserva estoque antes de simular a falha não seria detectado.

**Sugestão:** Adicionar ao Cenário 3 (análogo à verificação do Cenário 2):
```bash
STOCK_APOS=$(get_stock "$PRODUCT_ID")
if [[ "$STOCK_APOS" -eq "$STOCK_ANTES_C3" ]]; then
    cenario_ok "Estoque nao alterado: $STOCK_APOS (inventario falhou antes de reservar)"
else
    cenario_fail "Estoque alterado inesperadamente: era $STOCK_ANTES_C3, agora $STOCK_APOS"
fi
```

---

## INFO

---

### [INFO-01] `POST /inventory/reset` sem autenticação — endpoint destrutivo exposto

**Arquivo:** `src/InventoryService/Program.cs:46`

**Descrição:**
```csharp
app.MapPost("/inventory/reset", async (HttpContext context, InventoryRepository repo) => { ... });
```
O endpoint resetar estoque e deletar todas as reservas de um produto é acessível sem qualquer autenticação. Em ambiente containerizado isolado (PoC local), o risco é aceitável. Em qualquer ambiente compartilhado ou com porta exposta para rede, este endpoint permitiria disrupção de sagas em andamento por qualquer chamador.

**Sugestão:** Para evolução futura, proteger com header de API key ou restringir por `ASPNETCORE_ENVIRONMENT == Development`. Documentado no README como endpoint de "apenas desenvolvimento".

---

### [INFO-02] `EnsureTablesAsync` — log impreciso quando PROD-001 já existe com quantidade diferente

**Arquivo:** `src/InventoryService/InventoryRepository.cs:48`

**Descrição:**
```csharp
_logger.LogInformation("Tabelas ... Produto PROD-001 inserido com estoque inicial = 2 (se ainda nao existia).");
```
O `ON CONFLICT DO NOTHING` não atualiza o registro se já existe. Se o serviço reiniciar com PROD-001 em estoque = 0, o log diz "inserido com estoque inicial = 2" mas o produto **não foi atualizado**. Pode confundir diagnóstico de "por que o estoque está em 0 se o log diz 2?".

**Sugestão:** Ajustar o log para refletir o comportamento real: `"garantido (insert ignorado se ja existia)"` ou executar uma query de verificação do valor atual e logar esse valor.

---

### [INFO-03] `SagaOrchestrator/Worker.cs` — `ProcessReplyAsync` não valida se o reply type corresponde ao estado atual da saga

**Arquivo:** `src/SagaOrchestrator/Worker.cs:83`

**Descrição:**
O orquestrador poleia três filas de reply (`PaymentReplies`, `InventoryReplies`, `ShippingReplies`) e, ao receber um reply, busca a saga pelo `SagaId` e avança o estado independentemente de qual fila o reply veio. Não há verificação como: "esperamos `InventoryReply` pois saga está em `InventoryReserving`, mas chegou `PaymentReply`?". Em condições normais isso não ocorre, mas em cenários de re-entrega cruzada (ex: timeout de visibilidade e reprocessamento fora de ordem), poderia avançar a saga com o reply do step errado.

**Sugestão:** Para robustez, validar se `mapping.ReplyTypeName` é esperado para `saga.CurrentState`. Aceitável como dívida técnica para PoC.

---

### [INFO-04] `docker-compose.yml` — `start_period: 15s` pode ser insuficiente em cold start de primeira build

**Arquivo:** `docker-compose.yml:72`

**Descrição:**
```yaml
start_period: 15s
```
O `start_period` define o tempo de grace antes de contabilizar falhas de health check. Em primeiro build (download de imagens base, restauração de pacotes NuGet), os serviços .NET 10 podem levar >15s para inicializar. O Docker Compose poderia marcar o serviço como unhealthy prematuramente, causando falha em `depends_on: condition: service_healthy`.

**Sugestão:** Para primeira execução, considerar `start_period: 30s` ou instruir no README a usar `docker compose build` antes de `docker compose up`.

---

## Referências de Arquivo

| Arquivo | Findings |
|---------|----------|
| `scripts/concurrent-saga-demo.sh` | HIGH-01, MEDIUM-06, LOW-03 |
| `src/SagaOrchestrator/Worker.cs` | HIGH-02, MEDIUM-04, MEDIUM-05, LOW-02, INFO-03 |
| `src/InventoryService/InventoryRepository.cs` | MEDIUM-01, MEDIUM-02, INFO-02 |
| `src/InventoryService/Worker.cs` | MEDIUM-03 |
| `src/Shared/Idempotency/IdempotencyStore.cs` | LOW-01 |
| `scripts/happy-path-demo.sh` | LOW-04 |
| `src/InventoryService/Program.cs` | INFO-01 |
| `docker-compose.yml` | INFO-04 |
