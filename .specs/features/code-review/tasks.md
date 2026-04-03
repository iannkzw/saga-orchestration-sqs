# Code Review — Tasks de Ajuste e Validação

**Spec**: `.specs/features/code-review/spec.md`
**Report**: `.specs/features/code-review/REPORT.md`
**Status**: Draft

---

## Execution Plan

### Phase 1: HIGH — Bugs funcionais críticos (Sequential)

Corrigir primeiro; bloqueiam demos e podem corromper estado.

```
T1 → T2
```

### Phase 2: MEDIUM — Edge cases e riscos (Parallel OK)

Independentes entre si após Phase 1.

```
       ┌→ T3  ─┐
       ├→ T4  ─┤
T2 ────┼→ T5  ─┼──→ T11
       ├→ T6  ─┤
       └→ T7  ─┘
T8 ─────────────→ T11   (independente)
```

### Phase 3: LOW — Robustez e qualidade de log (Parallel OK)

```
T9 ─┐
T10 ─┤──→ T11
T11 ─┘
T12 ─→ T11
```

### Phase 4: INFO — Validar e documentar dívidas técnicas (Sequential ao final)

```
T13 → T14 → T15 → T16
```

---

## Task Breakdown

---

### T1: Corrigir grep case-sensitive no modo `--no-lock`

**Finding**: HIGH-01
**What**: Adicionar flag `-i` ao `grep` que verifica o modo sem lock para torná-lo case-insensitive.
**Where**: `scripts/concurrent-saga-demo.sh:64`
**Depends on**: None

**Mudança**:
```bash
# antes
docker logs saga-inventory-service 2>&1 | grep -q "locking=SEM LOCK"
# depois
docker logs saga-inventory-service 2>&1 | grep -qi "locking=sem lock"
```

**Done when**:
- [ ] Flag `-i` adicionada ao grep da verificação de modo `--no-lock`
- [ ] Script executado com `--no-lock` não aborta na verificação de container mode
- [ ] Teste manual: `bash scripts/concurrent-saga-demo.sh --no-lock` passa a etapa de verificação sem erro

---

### T2: Documentar dual-write não-atômico (HIGH-02)

**Finding**: HIGH-02
**What**: Adicionar comentário inline `// TODO: Transactional Outbox` no `HandleSuccessAsync` e `HandleFailureAsync` explicando o risco de dual-write e a estratégia de mitigação recomendada. Como alternativa mínima para PoC, inverter a ordem: enviar o comando SQS *antes* do `SaveChangesAsync`.
**Where**: `src/SagaOrchestrator/Worker.cs:138` (HandleSuccessAsync) e HandleFailureAsync correspondente
**Depends on**: T1

> **Decisão de escopo**: Implementar Transactional Outbox completo está fora deste ciclo. A correção mínima (inversão de ordem) é aplicada; o TODO documenta a dívida.

**Mudança**:
```csharp
// HandleSuccessAsync e HandleFailureAsync — inverter a ordem
// 1. Enviar comando SQS primeiro (idempotency no consumidor absorve reenvio)
await SendCommandToQueueAsync(..., ct);
// 2. Persistir a transição de estado
await db.SaveChangesAsync(ct);
// TODO [Transactional Outbox]: salvar comando na mesma tx do DB e publicar via job separado
//      para garantir entrega exactly-once sem dual-write.
```

**Done when**:
- [ ] Ordem invertida em `HandleSuccessAsync` e `HandleFailureAsync`
- [ ] Comentário TODO com descrição do risco e workaround adicionado
- [ ] Projeto compila sem erros (`dotnet build`)
- [ ] Happy-path demo executa sem regressão

---

### T3: Adicionar transação explícita em `ResetStockAsync`

**Finding**: MEDIUM-01
**What**: Envolver os dois statements SQL de `ResetStockAsync` em `BeginTransactionAsync` / `CommitAsync`.
**Where**: `src/InventoryService/InventoryRepository.cs:582`
**Depends on**: None

**Mudança**:
```csharp
await using var tx = await conn.BeginTransactionAsync(ct);
// cmd.Transaction = tx para os dois comandos
await tx.CommitAsync(ct);
```

**Done when**:
- [ ] `BeginTransactionAsync` chamado antes dos dois statements
- [ ] `cmd.Transaction = tx` atribuído em ambos os comandos
- [ ] `CommitAsync` chamado após execução dos dois
- [ ] Projeto compila sem erros
- [ ] Reset de estoque via `POST /inventory/reset` funciona corretamente nos demos

---

### T4: Incrementar `version` em `ReleaseAsync`

**Finding**: MEDIUM-02
**What**: Adicionar `version = version + 1` ao UPDATE do `ReleaseAsync` para manter o invariante do locking otimista.
**Where**: `src/InventoryService/InventoryRepository.cs:490`
**Depends on**: None

**Mudança**:
```csharp
// antes
"UPDATE inventory SET quantity = quantity + @qty WHERE product_id = @productId"
// depois
"UPDATE inventory SET quantity = quantity + @qty, version = version + 1 WHERE product_id = @productId"
```

**Done when**:
- [ ] UPDATE do `ReleaseAsync` inclui `version = version + 1`
- [ ] Projeto compila sem erros
- [ ] Demo concorrente com locking otimista não apresenta overbooking em execução manual

---

### T5: Passar `sagaId` real para `StartSendReply` em `SendReplyAsync`

**Finding**: MEDIUM-03
**What**: Adicionar parâmetro `Guid sagaId` ao método `SendReplyAsync` e substituir `string.Empty` pela string do GUID real.
**Where**: `src/InventoryService/Worker.cs:278`
**Depends on**: None

**Mudança**:
```csharp
// assinatura
private async Task SendReplyAsync<T>(string repliesQueueUrl, T reply, Guid sagaId, CancellationToken ct)
// uso
using (SagaActivitySource.StartSendReply(typeof(T).Name, sagaId.ToString()))

// call sites: passar reply.SagaId
await SendReplyAsync(repliesQueueUrl, reply, reply.SagaId, ct);
```

**Done when**:
- [ ] Assinatura de `SendReplyAsync` inclui parâmetro `Guid sagaId`
- [ ] `StartSendReply` recebe `sagaId.ToString()` em vez de `string.Empty`
- [ ] Ambos os call sites passam `reply.SagaId`
- [ ] Projeto compila sem erros
- [ ] Span `send InventoryReply` tem a tag `saga.id` preenchida (verificável nos logs do OTEL ou via inspeção do código)

---

### T6: Logar exceção em `DeserializeItems`

**Finding**: MEDIUM-04
**What**: Substituir o `catch` silencioso em `DeserializeItems` por um `catch (Exception ex)` que loga com `LogError` antes de retornar `[]`.
**Where**: `src/SagaOrchestrator/Worker.cs:354`
**Depends on**: None

**Mudança**:
```csharp
catch (Exception ex)
{
    _logger.LogError(ex, "Falha ao desserializar ItemsJson da saga. Json={ItemsJson}", itemsJson);
    return [];
}
```

**Done when**:
- [ ] `catch` captura `Exception ex` (não bare `catch`)
- [ ] `LogError` chamado com a exception e o `itemsJson`
- [ ] Projeto compila sem erros

---

### T7: Cachear URLs das command queues no startup do `SagaOrchestrator`

**Finding**: MEDIUM-05
**What**: Adicionar campo `_commandQueueUrls` (Dictionary<string, string>) ao Worker, populado no `ExecuteAsync` startup junto com as reply queues, e usar esse cache em `SendCommandToQueueAsync`.
**Where**: `src/SagaOrchestrator/Worker.cs:309`
**Depends on**: None

**Mudança**:
```csharp
// campo
private readonly Dictionary<string, string> _commandQueueUrls = new();

// no startup (ExecuteAsync)
foreach (var queueName in commandQueueNames)
{
    var r = await _sqs.GetQueueUrlAsync(queueName, ct);
    _commandQueueUrls[queueName] = r.QueueUrl;
}

// em SendCommandToQueueAsync
var queueUrl = _commandQueueUrls[commandQueue];
// remover: var queueUrlResponse = await _sqs.GetQueueUrlAsync(commandQueue, ct);
```

**Done when**:
- [ ] `_commandQueueUrls` declarado e populado no startup
- [ ] `SendCommandToQueueAsync` usa o cache em vez de chamar `GetQueueUrlAsync` por mensagem
- [ ] Projeto compila sem erros
- [ ] Demo concorrente executa sem erros de resolução de queue URL

---

### T8: Documentar requisito Bash 4 no README e adicionar verificação no script

**Finding**: MEDIUM-06
**What**: Adicionar verificação de versão do Bash no topo de `concurrent-saga-demo.sh` e documentar pré-requisito (`bash >= 4`) no README.
**Where**: `scripts/concurrent-saga-demo.sh:1-10` e `README.md`
**Depends on**: None

**Mudança no script**:
```bash
# logo após o shebang
if [[ "${BASH_VERSINFO[0]}" -lt 4 ]]; then
  echo "ERRO: requer Bash >= 4. No macOS: brew install bash"
  exit 1
fi
```

**Mudança no README**: Adicionar seção de pré-requisitos mencionando `bash >= 4` (e instrução `brew install bash` para macOS).

**Done when**:
- [ ] Verificação de versão adicionada ao topo do script
- [ ] README atualizado com pré-requisito Bash 4+
- [ ] Executar com Bash 3 imprime mensagem de erro clara e sai com código 1

---

### T9: Distinguir inserção nova de conflito em `IdempotencyStore.SaveAsync`

**Finding**: LOW-01
**What**: Verificar `rowsAffected` do `ExecuteNonQueryAsync` e logar `LogInformation` só quando `> 0`, `LogDebug` quando `= 0` (key já existia).
**Where**: `src/Shared/Idempotency/IdempotencyStore.cs:72`
**Depends on**: None

**Mudança**:
```csharp
var rowsAffected = await cmd.ExecuteNonQueryAsync();
if (rowsAffected > 0)
    _logger.LogInformation("Idempotency save: chave {Key} salva para saga {SagaId}", idempotencyKey, sagaId);
else
    _logger.LogDebug("Idempotency save: chave {Key} ja existia (ON CONFLICT), ignorado", idempotencyKey);
```

**Done when**:
- [ ] Retorno de `ExecuteNonQueryAsync` capturado em variável
- [ ] Log diferenciado por `rowsAffected`
- [ ] Projeto compila sem erros

---

### T10: Validar campos obrigatórios com `TryGetProperty` em `ProcessReplyAsync`

**Finding**: LOW-02
**What**: Substituir `GetProperty` por `TryGetProperty` para `SagaId` e `Success`. Se inválida, logar e deletar a mensagem em vez de deixá-la entrar em retry loop.
**Where**: `src/SagaOrchestrator/Worker.cs:90`
**Depends on**: None

**Mudança**:
```csharp
if (!baseReply.TryGetProperty("SagaId", out var sagaIdProp) ||
    !baseReply.TryGetProperty("Success", out var successProp))
{
    _logger.LogError("Mensagem malformada em {Queue}: {Body}", mapping.QueueName, message.Body);
    await _sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, ct);
    return;
}
var sagaId = sagaIdProp.GetGuid();
var success = successProp.GetBoolean();
```

**Done when**:
- [ ] `TryGetProperty` usado para `SagaId` e `Success`
- [ ] Mensagem malformada é deletada com log de erro
- [ ] Projeto compila sem erros

---

### T11: Adicionar `trap EXIT` para cleanup do `TMP_DIR`

**Finding**: LOW-03
**What**: Adicionar `trap 'rm -rf "$TMP_DIR"' EXIT` logo após a criação do diretório temporário.
**Where**: `scripts/concurrent-saga-demo.sh:106`
**Depends on**: None

**Mudança**:
```bash
TMP_DIR=$(mktemp -d)
trap 'rm -rf "$TMP_DIR"' EXIT
```

**Done when**:
- [ ] `trap` adicionado imediatamente após `mktemp -d`
- [ ] Simular abort manual (`Ctrl+C`) e verificar que o diretório `/tmp/tmp.*` foi removido

---

### T12: Adicionar verificação de estoque no Cenário 3 do `happy-path-demo.sh`

**Finding**: LOW-04
**What**: Capturar `STOCK_ANTES_C3` antes de executar o Cenário 3 e adicionar assertiva pós-execução comparando com o estoque atual.
**Where**: `scripts/happy-path-demo.sh:250`
**Depends on**: None

**Mudança**:
```bash
STOCK_ANTES_C3=$(get_stock "$PRODUCT_ID")

# ... executa cenário 3 ...

STOCK_APOS=$(get_stock "$PRODUCT_ID")
if [[ "$STOCK_APOS" -eq "$STOCK_ANTES_C3" ]]; then
    cenario_ok "Estoque nao alterado: $STOCK_APOS (inventario falhou antes de reservar)"
else
    cenario_fail "Estoque alterado inesperadamente: era $STOCK_ANTES_C3, agora $STOCK_APOS"
fi
```

**Done when**:
- [ ] `STOCK_ANTES_C3` capturado antes do cenário
- [ ] Assertiva de comparação adicionada ao final do Cenário 3
- [ ] Demo happy-path executa sem falsos positivos no Cenário 3

---

### T13: Restringir `POST /inventory/reset` a ambiente Development

**Finding**: INFO-01
**What**: Envolver o `MapPost("/inventory/reset")` em condição `if (app.Environment.IsDevelopment())` e adicionar comentário de aviso.
**Where**: `src/InventoryService/Program.cs:46`
**Depends on**: None

**Mudança**:
```csharp
// Endpoint destrutivo — apenas para ambiente de desenvolvimento/demo
if (app.Environment.IsDevelopment())
{
    app.MapPost("/inventory/reset", async (HttpContext context, InventoryRepository repo) => { ... });
}
```

**Done when**:
- [ ] Endpoint protegido por `IsDevelopment()`
- [ ] Comentário de aviso adicionado
- [ ] Projeto compila sem erros
- [ ] Container rodando com `ASPNETCORE_ENVIRONMENT=Development` (padrão do docker-compose) continua funkcionando nos demos

---

### T14: Corrigir mensagem de log em `EnsureTablesAsync`

**Finding**: INFO-02
**What**: Ajustar o log para refletir a semântica real do `ON CONFLICT DO NOTHING` — produto pode não ter sido inserido se já existia.
**Where**: `src/InventoryService/InventoryRepository.cs:48`
**Depends on**: None

**Mudança**:
```csharp
_logger.LogInformation(
    "Tabelas verificadas. Produto PROD-001 garantido (inserido ou ja existia — ON CONFLICT DO NOTHING).");
```

**Done when**:
- [ ] Mensagem de log atualizada para refletir comportamento real
- [ ] Projeto compila sem erros

---

### T15: Adicionar TODO de validação de replay cross-state em `ProcessReplyAsync`

**Finding**: INFO-03
**What**: Adicionar comentário `// TODO` inline em `ProcessReplyAsync` documentando a dívida técnica de não validar se o reply type corresponde ao estado atual da saga.
**Where**: `src/SagaOrchestrator/Worker.cs:83`
**Depends on**: None

**Mudança**:
```csharp
// TODO [Dívida Técnica]: Validar se mapping.ReplyTypeName é esperado para saga.CurrentState.
// Em re-entrega cruzada por timeout de visibilidade, um reply de estado anterior poderia
// avançar a saga incorretamente. Aceitável para PoC; mitigar em produção.
```

**Done when**:
- [ ] Comentário TODO adicionado antes do bloco de avanço de estado
- [ ] Projeto compila sem erros

---

### T16: Aumentar `start_period` no docker-compose e documentar no README

**Finding**: INFO-04
**What**: Aumentar `start_period` de `15s` para `30s` no `docker-compose.yml` e adicionar nota no README recomendando `docker compose build` antes do primeiro `up`.
**Where**: `docker-compose.yml:72` e `README.md`
**Depends on**: None

**Mudança no docker-compose**:
```yaml
start_period: 30s
```

**Mudança no README**: Adicionar nota em "Como executar": "Na primeira execução, rode `docker compose build` antes de `docker compose up` para evitar timeout de health check durante restauração de pacotes NuGet."

**Done when**:
- [ ] `start_period: 30s` aplicado a todos os serviços .NET no docker-compose
- [ ] README atualizado com instrução de first-run
- [ ] `docker compose up` em ambiente limpo inicia sem `unhealthy` prematuro

---

## Resumo por Severidade

| Fase | Findings | Tasks | Prioridade |
|------|----------|-------|------------|
| 1 | HIGH-01, HIGH-02 | T1, T2 | Imediata |
| 2 | MEDIUM-01…06 | T3–T8 | Alta |
| 3 | LOW-01…04 | T9–T12 | Média |
| 4 | INFO-01…04 | T13–T16 | Baixa |
