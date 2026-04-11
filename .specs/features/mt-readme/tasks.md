# Tasks: mt-readme

**Feature:** mt-readme
**Milestone:** M10 - Migração MassTransit

## Resumo

4 tarefas. Deve ser executada após `mt-docs-didaticos` (links para novos docs) e `mt-program-config` (exemplos reais de request/response). Tarefas T1-T3 são independentes entre si.

---

## T1 — Atualizar diagrama de arquitetura e stack

**Arquivo:** `README.md`

**O que fazer:**

1. Substituir diagrama ASCII de 5 serviços por 4 serviços
2. Adicionar `MassTransit` e `MassTransit.AmazonSQS` na lista de tecnologias
3. Remover menção ao `SagaOrchestrator` como serviço separado
4. Adicionar parágrafo sobre "Por que MassTransit?" (2-3 linhas)

**Verificação:** README renderiza corretamente no GitHub. Diagrama reflete arquitetura atual.

---

## T2 — Atualizar Quick Start e exemplos de request/response

**Arquivo:** `README.md`

**O que fazer:**

1. Verificar que `docker compose up` no Quick Start ainda é válido
2. Remover qualquer referência a `localhost:5000` (porta do SagaOrchestrator)
3. Atualizar exemplo de `POST /orders` se o request body ou response mudou
4. Atualizar exemplo de `GET /orders/{id}` para mostrar campo `status: Completed`
5. Remover exemplo de `GET /sagas/{id}` (não existe mais)

**Verificação:** Exemplos copy-pastáveis funcionam em terminal após `docker compose up`.

---

## T3 — Atualizar seções de scripts e cenários de falha

**Arquivo:** `README.md`

**O que fazer:**

1. Atualizar referências aos scripts: `happy-path-demo.sh`, `concurrent-saga-demo.sh`
2. Remover referência ao `init-sqs.sh` (não necessário com MassTransit)
3. Atualizar cenários de falha para mencionar compensação via state machine declarativa
4. Mencionar `--mode optimistic` e `--mode pessimistic` nos scripts de concorrência

**Verificação:** Seção de scripts reflete os scripts existentes atualmente.

---

## T4 — Atualizar índice de documentação

**Arquivo:** `README.md`

**O que fazer:**

1. Adicionar links para:
   - `docs/09-masstransit-state-machine.md`
   - `docs/10-outbox-pattern.md`
2. Verificar que links para docs `01` a `08` ainda são válidos
3. Adicionar link para guia de migração `docs/masstransit-migration/` (se existir)

**Verificação:** Todos os links no índice funcionam (`[text](path)`).

---

## Dependências

```
mt-docs-didaticos (novos docs existem) → T4
mt-program-config (request/response real) → T2
T1, T3 independentes
```
