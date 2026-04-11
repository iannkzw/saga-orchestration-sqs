# Tasks: mt-cleanup

**Feature:** mt-cleanup
**Milestone:** M10 - Migração MassTransit

## Resumo

4 tarefas de remoção. Deve ser executada APÓS `mt-program-config` estar validado (fluxo end-to-end funcionando). Todas as tarefas são independentes entre si, exceto T4 (validação final).

---

## T1 — Remover projeto SagaOrchestrator

**O que fazer:**

1. Remover diretório `src/SagaOrchestrator/` completo
2. Remover linha do projeto na solution:
   ```bash
   dotnet sln remove src/SagaOrchestrator/SagaOrchestrator.csproj
   ```
3. Verificar que nenhum `.csproj` referencia `SagaOrchestrator`

**Verificação:** `dotnet build` na solução completa sem erros após remoção.

---

## T2 — Remover contratos legados do Shared

**Diretório:** `src/Shared/`

**O que fazer:**

1. Remover arquivos de contratos antigos:
   - `*Command.cs` (versões antigas: `ProcessPaymentCommand`, etc.)
   - `*Reply.cs` (`PaymentReply`, `InventoryReply`, `ShippingReply`)
   - Qualquer `SagaStartedMessage.cs` ou similar
2. Remover `IIdempotencyStore.cs` e `IdempotencyStore.cs` (se ainda existirem)
3. Remover helpers legados de SQS (`SqsHelper.cs`, `MessageSerializer.cs`) — apenas os que foram substituídos pelo MassTransit

**Verificação:** `dotnet build src/Shared/` sem erros. `grep -r "PaymentReply\|InventoryReply\|ShippingReply" src/` sem resultados.

---

## T3 — Remover variáveis de ambiente e infraestrutura obsoleta

**Arquivos:** `docker-compose.yml`, `scripts/init-sqs.sh` (se existir)

**O que fazer:**

1. Remover do `docker-compose.yml`:
   - Serviço `saga-orchestrator`
   - Variáveis de ambiente obsoletas de todos os serviços (ver spec.md)
   - Manter `INVENTORY_LOCKING_ENABLED` como fallback comentado

2. Remover ou esvaziar `scripts/init-sqs.sh` — filas criadas automaticamente pelo MassTransit
   - Manter o arquivo com comentário explicativo se for didaticamente relevante

**Verificação:** `docker compose config` sem referências ao serviço `saga-orchestrator`.

---

## T4 — Validação final após cleanup

**O que fazer:**

1. `dotnet build` na solução completa — sem erros
2. `docker compose up` — 4 serviços sobem (sem `saga-orchestrator`)
3. `POST /orders` → `GET /orders/{id}` → `status: Completed`
4. Verificar DLQ: `GET /dlq` retorna lista vazia
5. `grep -r "SagaOrchestrator\|PaymentReply\|InventoryReply\|ShippingReply\|SAGA_ORCHESTRATOR_URL" src/` — sem resultados

**Verificação:** Checklist da spec.md 100% completo.

---

## Dependências

```
mt-program-config (fluxo end-to-end validado) → T1, T2, T3
T1, T2, T3 → T4 (validação após todas as remoções)
```
