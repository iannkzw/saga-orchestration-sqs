# Feature: mt-docs-didaticos

**Milestone:** M10 - Migração MassTransit
**Status:** PLANNED

## Objetivo

Reescrever os 8 documentos existentes em `docs/` para refletir a arquitetura MassTransit, e criar 2 novos documentos específicos da migração. O conteúdo deve manter o caráter didático em português para engenheiros backend.

## Documentos Existentes (reescrever)

| Arquivo | Título Atual | Mudanças Necessárias |
|---------|-------------|---------------------|
| `docs/01-visao-geral.md` | Visão geral da saga | Atualizar diagrama: 4 serviços, MassTransit, sem hop HTTP |
| `docs/02-fluxo-happy-path.md` | Fluxo happy path | Substituir command/reply por publish/subscribe, nova sequência |
| `docs/03-compensacao.md` | Compensação | Atualizar cadeia de compensação MassTransit declarativa |
| `docs/04-idempotencia.md` | Idempotência | Substituir IdempotencyStore por DuplicateDetectionWindow + correlação |
| `docs/05-estado-saga.md` | Estado da saga | Substituir SagaOrchestrator por OrderStateMachine, nova tabela |
| `docs/06-dlq-visibilidade.md` | DLQ e visibilidade | Atualizar para filas `*_error` do MassTransit |
| `docs/07-concorrencia-sagas.md` | Concorrência | Atualizar: pessimistic, optimistic (existente), + MassTransit ConcurrencyMode |
| `docs/08-observabilidade.md` | Observabilidade | Mencionar rastreabilidade via MassTransit (Activity, spans) |

## Documentos Novos (criar)

### `docs/09-masstransit-state-machine.md`
**Título:** State Machine Declarativa com MassTransit

Conteúdo:
- O que é uma state machine declarativa vs imperativa
- Anatomia da `MassTransitStateMachine<T>`: State, Event, Initially, During
- Como o MassTransit correlaciona eventos (CorrelationId)
- Fluxo happy path passo a passo no código
- Fluxo de compensação passo a passo
- Comparativo: código antes (switch/case manual) vs depois (declarativo)

### `docs/10-outbox-pattern.md`
**Título:** Outbox Pattern: Atomicidade entre DB e Mensageria

Conteúdo:
- O problema do dual-write (por que é um problema)
- Solução: Transactional Outbox Pattern
- Como o MassTransit EF Core Outbox implementa o padrão
- Tabelas criadas: `outbox_message`, `outbox_state`, `inbox_state`
- DuplicateDetectionWindow e deduplicação
- Diagrama: fluxo com e sem outbox

## Padrão de Documento

Cada documento deve ter:
```markdown
# Título do Documento

## O que é (1-2 parágrafos conceituais)

## Como funciona neste projeto (diagrama ou sequência)

## No código (snippets relevantes com explicação)

## Pontos de atenção (armadilhas, trade-offs)

## Próximos passos / Leitura complementar
```

## Critérios de Aceite

1. Todos os 8 documentos existentes atualizados
2. 2 novos documentos criados (`09-masstransit-state-machine.md`, `10-outbox-pattern.md`)
3. Nenhum documento menciona `SagaOrchestrator` como serviço separado
4. Diagramas de sequência refletem 4 serviços e fluxo MassTransit
5. Snippets de código são do código real do repositório (não hipotéticos)
