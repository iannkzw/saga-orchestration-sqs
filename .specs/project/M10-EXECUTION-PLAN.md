# M10 — Plano de Execucao da Migracao MassTransit

> **Plano de longa memoria.** Este documento e a fonte de verdade para retomar o trabalho do M10 entre sessoes. Cada fase tem checklist, criterios de aceite, code review e plano de teste. Ao concluir um item, marcar `[x]` e atualizar `STATE.md`.
>
> **Ultima atualizacao:** 2026-04-20
> **Autor da analise:** claude (sessao de planejamento)

---

## Diagnostico inicial (validado no codigo)

O ROADMAP declara 5 features como DONE, mas a inspecao do codigo revelou que **`mt-merge-order-orchestrator` foi marcada como DONE prematuramente**. O que existe hoje:

- MassTransit esta configurado nos 4 servicos e funcionando (state machine, consumers, eventos tipados, UsingAmazonSqs).
- **Porem o projeto `src/SagaOrchestrator/` continua existindo, esta na solution, no docker-compose e tem `Worker.cs` ativo com polling manual.** Isso cria arquitetura dual (MassTransit novo + SQS manual legado convivendo).
- `OrderService/Program.cs` ainda usa `ConcurrencyMode.Pessimistic` e `UseInMemoryOutbox` (nao persiste).
- `IdempotencyStore.cs`, `SqsConfig.cs`, `SqsTracePropagation.cs`, contratos `Replies/*` ainda existem (mortos em MassTransit mas vivos em SagaOrchestrator).
- Sem migrations EF formais (ainda usa `CreateTablesAsync`).
- `SagaClient` dos testes aponta para porta 5002 (SagaOrchestrator).
- Scripts de demo e README ainda descrevem 5 containers.
- Existe documentacao `docs/masstransit-migration/` mas `docs/01-08.md` ainda falam do modelo antigo.
- `.specs/codebase/` nao existe.

**Implicacao:** o backlog de 11 features PLANNED nao pode comecar como se o trabalho de mt-merge estivesse pronto. A Fase 1 do plano abaixo e explicitamente "fechar a divida de mt-merge-order-orchestrator".

---

## Ordem logica de execucao

As fases abaixo estao em ordem de **dependencia tecnica**, nao de prioridade de negocio. Respeitar a ordem evita retrabalho.

| Fase | Nome | Bloqueia | Esforco |
|------|------|----------|---------|
| 1 | Finalizar mt-merge-order-orchestrator (remover SagaOrchestrator) | Todas | M |
| 2 | mt-db-migration (EF migrations formais) | Fase 3 | P |
| 3 | mt-outbox-dlq (Outbox EF Postgres) | Fases 4, 5 | G |
| 4 | mt-concurrency (RowVersion + Optimistic) | Fase 5 | P |
| 5 | mt-idempotency (remover IdempotencyStore) | Fase 6 | P |
| 6 | mt-cleanup (codigo morto + contratos antigos) | Fase 7 | P |
| 7 | mt-infra-docker (compose/init-sqs/init-db) | Fase 8 | P |
| 8 | mt-program-config (finish wiring) | Fase 9 | P |
| 9 | mt-integration-tests (adaptar fixtures + cenarios novos) | Fase 10 | M |
| 10 | mt-demo-scripts (4 servicos) | — | XP |
| 11 | mt-readme + mt-docs-didaticos (docs 01-10) | — | G |
| 12 | mt-codebase-docs (.specs/codebase/*) | — | M |

**Legenda:** XP=muito pequeno, P=pequeno, M=medio, G=grande.

**Heuristica de code review:** ao final de **cada fase** rodar o roteiro da secao "Code review por fase" antes de marcar como DONE.

---

## Fase 1 — Finalizar mt-merge-order-orchestrator

**Objetivo:** eliminar a arquitetura dual. Apos esta fase, o repo tem 4 containers .NET e zero referencias a `SagaOrchestrator`.

### Pre-requisitos
- Build da solution verde hoje (validar antes de comecar).
- Backup/branch atual preservado.

### Checklist
- [x] Mover endpoint `GET /sagas/{id}` de `src/SagaOrchestrator/Program.cs` para `src/OrderService/Api/SagaEndpoints.cs`. Query le `order_saga_instances` via `OrderDbContext.SagaInstances`.
- [x] Endpoints `GET /dlq` e `POST /dlq/redrive` removidos nesta fase (sem substituto antes do Outbox — Fase 3).
- [x] Remover `src/SagaOrchestrator/` inteiro (csproj, Program.cs, Worker.cs, Data/, Models/, StateMachine/, Dockerfile).
- [x] Remover `SagaOrchestrator` de `SagaOrchestration.sln` (`dotnet sln remove src/SagaOrchestrator/SagaOrchestrator.csproj`).
- [x] Remover servico `saga-orchestrator` de `docker-compose.yml`.
- [x] Remover variavel `SAGA_ORCHESTRATOR_URL` e `SAGA_ORCHESTRATOR_PORT` de `.env` e docker-compose.
- [x] Nenhum `HttpClient` para SagaOrchestrator existia no OrderService (grep validado).
- [x] Atualizar `tests/IntegrationTests/Infrastructure/DockerComposeFixture.cs`: 4 health endpoints, RequiredQueues simplificado.
- [x] Atualizar `tests/IntegrationTests/Infrastructure/SagaClient.cs` para usar porta 5001, remover `_sagaClient` legado. Adaptar tests para estado "Final" (MassTransit).
- [x] Rodar `dotnet build` na solution — compilacao com 0 erros, 0 avisos.

### Code review
- Nenhum arquivo ainda contem `SagaOrchestrator` (grep case-insensitive).
- Nenhum arquivo ainda contem `saga-orchestrator` (kebab case).
- Nenhum arquivo ainda contem porta `5002` como default.
- `SagaOrchestration.sln` tem exatamente 5 projetos (OrderService, PaymentService, InventoryService, ShippingService, Shared, IntegrationTests). **Nota:** a solution tem 6 entradas se contar Shared + IntegrationTests como projetos separados. Ajustar o numero durante execucao.
- `docker-compose.yml` tem 4 servicos `.NET` (order, payment, inventory, shipping).
- Worker.cs nao existe em nenhum dos 4 servicos.

### Teste manual
1. `docker compose down -v && docker compose up --build -d`
2. Todos os 4 containers .NET devem ficar healthy (`docker compose ps`).
3. `curl -X POST http://localhost:5001/orders` com payload de happy path → retorna OrderId + SagaId.
4. `curl http://localhost:5001/sagas/{sagaId}` (endpoint migrado) → retorna estado atual.
5. Aguardar ~10s e consultar novamente → deve estar em `Completed`.
6. Verificar no banco: `SELECT status FROM orders WHERE id=...` → `Completed`.

### Teste automatizado
- `dotnet test tests/IntegrationTests/` — os 7 cenarios existentes devem passar (happy path, 3 compensacoes, isolamento/idempotencia, 2 de concorrencia).
- Se algum quebrar, corrigir antes de fechar a fase (tipicamente sera `SagaClient` ou fixture).

### Criterio de DONE
Build verde + testes integracao verdes + demo manual do happy path funcionando **sem** o container saga-orchestrator.

---

## Fase 2 — mt-db-migration (migrations EF formais)

**Objetivo:** parar de usar `CreateTablesAsync()` e mover para `dotnet ef migrations`. E pre-requisito para Outbox (que adiciona tabelas).

### Checklist
- [x] Instalar/confirmar ferramenta: `dotnet tool install --global dotnet-ef --version 10.*-*`.
- [x] Adicionar pacote `Microsoft.EntityFrameworkCore.Design` no `OrderService.csproj`.
- [x] Rodar `dotnet ef migrations add InitialCreate --project src/OrderService --output-dir Migrations` — gera snapshot do estado atual (Orders + OrderSagaInstance).
- [x] Substituir `CreateTablesAsync()` em `Program.cs` por `await db.Database.MigrateAsync()`.
- [x] Reduzir `infra/postgres/init.sql` para apenas extensoes (`uuid-ossp`). DDL das tabelas vira das migrations.
- [x] Confirmar que `SagaDbContext` (se ainda existir em Shared ou residuo de SagaOrchestrator) foi removido na Fase 1.

### Code review
- Pasta `src/OrderService/Migrations/` existe com `InitialCreate.cs` + `OrderDbContextModelSnapshot.cs`.
- Nenhum codigo chama `EnsureCreated()` ou `CreateTablesAsync()`.
- `init.sql` nao declara tabelas de dominio.

### Teste manual
1. `docker compose down -v` (zera volume do postgres).
2. `docker compose up --build -d`.
3. OrderService loga `Applying migration 'InitialCreate'` no startup.
4. `psql` no container e `\dt` — vejo `orders`, `order_saga_instances`, `inventory`, `inventory_reservations`.
5. Happy path do curl continua funcionando.

### Criterio de DONE
Startup aplica migration limpa em DB vazio + happy path passa.

---

## Fase 3 — mt-outbox-dlq (EF Outbox) ✓ DONE

**Status:** CONCLUIDA em 2026-04-12. Ver decisao em STATE.md.

**Objetivo:** resolver o dual-write entre state machine e publish. Toda mensagem sai na mesma transacao que o saga state.

### Checklist
- [x] Adicionar pacote `MassTransit.EntityFrameworkCore` ao `OrderService.csproj` (se ja nao estiver).
- [x] No `OrderDbContext.OnModelCreating`:
  ```csharp
  modelBuilder.AddInboxStateEntity();
  modelBuilder.AddOutboxMessageEntity();
  modelBuilder.AddOutboxStateEntity();
  ```
- [x] No `Program.cs` do OrderService, dentro do `AddMassTransit`:
  ```csharp
  cfg.AddEntityFrameworkOutbox<OrderDbContext>(o =>
  {
      o.UsePostgres();
      o.UseBusOutbox();
      o.QueryDelay = TimeSpan.FromSeconds(1);
      o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
  });
  ```
- [x] Na `OrderStateMachineDefinition`, remover `UseInMemoryOutbox(context)` — o EF outbox cobre isso.
- [x] Gerar migration: `dotnet ef migrations add AddMassTransitOutbox --project src/OrderService`.
- [x] Validar DDL gerado (tabelas `outbox_message`, `outbox_state`, `inbox_state`).
- [x] Remover endpoints `GET /dlq` e `POST /dlq/redrive` de qualquer lugar onde ainda existam (devem ter saido na Fase 1).
- [x] Adicionar retry policy na configuracao SQS (ex.: `UseMessageRetry(r => r.Intervals(1, 5, 10, 30))`).

### Code review
- `grep UseInMemoryOutbox` retorna 0 matches.
- `AddEntityFrameworkOutbox` aparece 1x no OrderService.
- Migration `AddMassTransitOutbox` existe e cria 3 tabelas.
- Saga publica eventos via `context.Publish`/`Send` dentro do state machine — nao usa `IBus` direto fora de transacao.
- DbContext usado pelo saga repository e o mesmo usado pelo outbox.

### Teste manual — happy path
1. `docker compose down -v && docker compose up --build -d`.
2. Postar um pedido happy path.
3. Saga chega a Completed.
4. `SELECT COUNT(*) FROM outbox_message WHERE ...` — mensagens processadas apareceram e foram drenadas (estado final vazio ou marcado).

### Teste manual — falha simulada de transporte
1. Matar LocalStack apos OrderCreated chegar ao saga.
2. Verificar que nenhuma mensagem e perdida: mensagens ficam em `outbox_message` com retry.
3. Subir LocalStack de novo → mensagens drenam e saga avanca.

### Teste automatizado
- Novo cenario em `IntegrationTests`: derrubar LocalStack por 5s no meio da saga e validar que o happy path eventualmente completa sem reprocessamento duplicado.

### Criterio de DONE
Happy path completo + teste de resiliencia + 0 uso de InMemoryOutbox.

---

## Fase 4 — mt-concurrency (RowVersion + Optimistic) ✓ DONE

**Status:** CONCLUIDA em 2026-04-20. Ver decisao em STATE.md.

### Checklist
- [x] Adicionar `public uint xmin { get; set; }` em `OrderSagaInstance` (coluna de sistema PostgreSQL — nao `byte[]`).
- [x] Em `OrderDbContext.OnModelCreating`: `modelBuilder.Entity<OrderSagaInstance>().Property(e => e.xmin).HasColumnName("xmin").IsRowVersion()` — Npgsql mapeia para `xid`.
- [x] Gerar migration `AddXminConcurrency` — gerada com `type: "xid"` e `rowVersion: true`.
- [x] Trocar `ConcurrencyMode.Pessimistic` por `ConcurrencyMode.Optimistic` no registro do saga repository.
- [x] Retry ajustado para 3 tentativas (100ms, 200ms, 500ms) — backoff curto para conflitos otimistas.

### Code review
- `grep ConcurrencyMode.Pessimistic` = 0.
- `grep IsRowVersion` > 0 em OrderDbContext.
- Migration `AddXminConcurrency` existe com `type: "xid"`.

### Teste manual
- Forcar conflito: disparar 2 eventos concorrentes para a mesma saga (ex.: reenviar um PaymentCompleted por retry manual no LocalStack) — state machine nao corrompe estado; retry absorve.

### Teste automatizado
- Reaproveitar cenario de concorrencia existente em IntegrationTests — deve continuar verde.
- Adicionar cenario: 10 sagas simultaneas atualizando o mesmo recurso compartilhado (inventory) e validar consistencia final.

### Criterio de DONE
Migration aplicada + cenario de concorrencia verde + retry visivel em logs sob conflito.

---

## Fase 5 — mt-idempotency (remover IdempotencyStore) ✓ DONE

**Status:** CONCLUIDA em 2026-04-20. Ver decisao em STATE.md.

### Checklist
- [x] Apagar `Shared/Idempotency/IdempotencyStore.cs` e `Shared/Models/IdempotencyRecord.cs`.
- [x] Remover tabela `idempotency_keys` do schema (criar migration `RemoveIdempotencyKeys` se a tabela ainda existia — senao pular). **Decisao:** tabela nunca foi criada (EnsureTableAsync nunca era chamado). Nenhuma migration necessaria.
- [x] Remover propriedade `IdempotencyKey` de `BaseCommand` e contratos derivados (ou remover o `BaseCommand` inteiro se ja nao for usado em MassTransit). **Decisao:** removida apenas a propriedade `IdempotencyKey`; `BaseCommand` mantido pois 9 commands ativos ainda herdam `SagaId` e `Timestamp`.
- [x] Grep por `TryGetAsync(` e `SaveAsync(` relacionados a idempotency — remover chamadas residuais.
- [x] Confirmar que a dedupe esta coberta pela dupla camada: saga correlation (state machine ignora eventos em estados invalidos) + Outbox `DuplicateDetectionWindow` (Fase 3).

### Code review
- `grep IdempotencyStore\|IdempotencyKey\|idempotency_keys` = 0 no src/.
- Nenhum arquivo da Shared referencia idempotency.

### Teste manual
- Reenviar o mesmo `OrderPlaced` 3 vezes via curl com mesmo correlation id → saga e criada 1 vez so (state machine ignora os replays).
- Reenviar um `PaymentCompleted` para uma saga ja em estado ShippingScheduling → state machine ignora silenciosamente.

### Teste automatizado
- Cenario existente de "idempotencia/isolamento" em IntegrationTests deve continuar verde sem mudancas.

### Criterio de DONE
Arquivos deletados + testes verdes + replay manual valida comportamento.

---

## Fase 6 — mt-cleanup (codigo morto residual) ✓ DONE

**Status:** CONCLUIDA em 2026-04-20. Ver decisao em STATE.md.

### Checklist
- [x] Remover `Shared/Configuration/SqsConfig.cs`.
- [x] Remover `Shared/Telemetry/SqsTracePropagation.cs`.
- [x] Remover `Shared/Contracts/Notifications/SagaTerminatedNotification.cs` + simplificar `init-sqs.sh` para no-op.
- [x] Remover `Shared/Contracts/Replies/*` (PaymentReply, InventoryReply, ShippingReply, ReleaseInventoryReply, RefundPaymentReply, CancelShippingReply, BaseReply).
- [x] `Shared/Contracts/Commands/BaseCommand.cs` — MANTIDO: 9 commands ativos herdam SagaId e Timestamp.
- [x] `ServiceCollectionExtensions.cs` — MANTIDO: AddSagaConnectivity registra IAmazonSQS para SqsConnectivityCheck no /health; AddSagaTracing/AddSagaLogging sao validos. Sem codigo de despacho manual.
- [x] Grep `CommandType` (message attribute de despacho manual) = 0 matches (ocorrencias em SagaActivitySource sao parametros de metodo, nao atributos de despacho).
- [x] `dotnet build` — 0 erros, 0 warnings de codigo (apenas NU1903 pre-existente).

### Code review
- Nenhum arquivo referencia classes removidas.
- `dotnet build` 0 erros, 0 warnings.
- Diff mostra so delecoes + imports removidos.

### Teste automatizado
- Suite completa de testes de integracao deve continuar verde.

### Criterio de DONE
Build verde + testes verdes + diff so com remocoes.

---

## Fase 7 — mt-infra-docker

### Checklist
- [ ] `docker-compose.yml`: confirmar 4 servicos .NET (ja feito na Fase 1, re-validar).
- [ ] `docker-compose.test.yml`: mesmo — 4 servicos.
- [ ] `infra/localstack/init-sqs.sh`: simplificar. Com MassTransit, as filas sao criadas automaticamente pelo `ConfigureEndpoints`. Ideal: reduzir o script a um no-op ou so a DLQs compartilhadas (se houver). Decidir durante execucao: **remover o script** ou **deixar so DLQ base**.
- [ ] `infra/postgres/init.sql`: manter apenas extensoes. Schemas vem das migrations.
- [ ] Health checks: 4 servicos.

### Code review
- Nenhuma referencia a `saga-orchestrator`.
- `init-sqs.sh` nao cria mais reply queues (`payment-replies`, `inventory-replies`, `shipping-replies`).

### Teste manual
- `docker compose down -v && docker compose up --build -d` → tudo sobe healthy.
- Listar filas no LocalStack:
  `aws --endpoint-url=http://localhost:4566 sqs list-queues` — filas criadas pelo MassTransit aparecem com nomes dos consumers.
- Happy path funciona.

### Criterio de DONE
Ambiente sobe do zero sem scripts de infra manuais + happy path passa.

---

## Fase 8 — mt-program-config (finish wiring)

Pequena fase de finalizacao: garantir que os 4 Program.cs estao no estado final canonico, sem restos de configs antigas.

### Checklist
- [ ] OrderService Program.cs: tem AddMassTransit + AddSagaStateMachine + AddEntityFrameworkOutbox + Minimal API, nada mais.
- [ ] PaymentService: so consumers Payment.
- [ ] InventoryService: so consumers Inventory.
- [ ] ShippingService: so consumers Shipping (incluindo **CancelShippingConsumer** se nao existir — o Explore apontou essa lacuna; criar se necessario, ou confirmar que compensacao de shipping e no-op por design).
- [ ] Cada Program.cs tem OpenTelemetry `AddSource("MassTransit")`.
- [ ] Nenhum registro de `HostedService<Worker>`.

### Code review
- Diff Program.cs: so MassTransit + EF + OTel + minimal API.
- Nenhum polling loop.

### Teste automatizado
- Suite de integracao completa verde.

### Criterio de DONE
4 Program.cs canonicos + build + testes verdes.

---

## Fase 9 — mt-integration-tests

### Checklist
- [ ] `DockerComposeFixture`: 4 containers.
- [ ] `SagaClient` aponta para OrderService (porta 5001).
- [ ] Reaproveitar os 7 cenarios existentes (ajustes minimos).
- [ ] **Novo cenario 1:** `Order.Status` atualiza para `Completed` no happy path (valida bug M9 resolvido).
- [ ] **Novo cenario 2:** `Order.Status` atualiza para `Failed` quando compensation chain termina.
- [ ] **Novo cenario 3:** resiliencia via Outbox — derrubar LocalStack 5s no meio e validar consistencia final (Fase 3).
- [ ] **Novo cenario 4:** concorrencia otimista — 10 eventos concorrentes na mesma saga, validar 0 corrupcao (Fase 4).
- [ ] **Novo cenario 5:** deduplicacao via DuplicateDetectionWindow — publicar o mesmo comando 2x dentro da janela e validar que handler executa 1x.

### Code review
- Todos os novos cenarios sao independentes e podem rodar em qualquer ordem.
- Fixture limpa estado entre cenarios (ou cada cenario usa ids unicos).

### Criterio de DONE
`dotnet test` todos verdes + novos cenarios documentados no proprio codigo de teste com comentarios curtos de intencao.

---

## Fase 10 — mt-demo-scripts

### Checklist
- [ ] `scripts/lib/common.sh`: URLs apontam para 4 servicos, sem `saga-orchestrator`.
- [ ] `scripts/happy-path-demo.sh`: 4 cenarios continuam reproduziveis.
- [ ] `scripts/concurrent-saga-demo.sh`: idem.

### Teste manual
- Rodar os 2 scripts em ambiente limpo.

### Criterio de DONE
Scripts passam sem intervencao manual.

---

## Fase 11 — mt-readme + mt-docs-didaticos

Esta fase e grande mas pode ser paralelizada em pull requests menores.

### Checklist — README.md
- [ ] Diagrama atualizado (4 servicos, OrderService como orquestrador).
- [ ] Tabela de portas (4 servicos).
- [ ] Exemplos curl no OrderService.
- [ ] Pre-requisitos: pacotes MassTransit.
- [ ] Sumario dos docs didaticos atualizado.

### Checklist — docs/01-08 (reescrita)
- [ ] `01-fundamentos-sagas.md`: adicionar secao sobre fusao Order+Orchestrator.
- [ ] `02-maquina-de-estados.md`: reescrever para MassTransit state machine declarativa.
- [ ] `03-padroes-compensacao.md`: compensacao tipada (sem CompensationDataJson).
- [ ] `04-idempotencia-retry.md`: dupla camada (correlation + Outbox).
- [ ] `05-sqs-dlq-visibility.md`: topologia simplificada + Outbox + retry policies.
- [ ] `06-opentelemetry-traces.md`: instrumentacao automatica MassTransit.
- [ ] `07-concorrencia-sagas.md`: RowVersion + ConcurrencyMode.Optimistic.
- [ ] `08-guia-pratico.md`: novos comandos curl, 4 servicos.

### Checklist — docs novos
- [ ] `09-masstransit-overview.md`: visao geral do framework.
- [ ] `10-comparativo-manual-vs-masstransit.md`: antes/depois com metricas (linhas, filas, containers, complexidade).

### Code review
- Cada doc usa screenshots/trechos de codigo extraidos do estado atual do repo.
- Links internos entre docs funcionam.
- Glossario consistente com o codigo (nomes de eventos, estados).

### Criterio de DONE
Revisao por leitura completa + links validados + metricas do doc 10 extraidas do git real.

---

## Fase 12 — mt-codebase-docs

### Checklist
- [ ] `.specs/codebase/STACK.md`: MassTransit + EF Core 10 + Npgsql + OTel + versoes.
- [ ] `.specs/codebase/ARCHITECTURE.md`: 4 microsservicos, OrderService como orquestrador, topologia SQS.
- [ ] `.specs/codebase/CONVENTIONS.md`: naming (Consumer/Definition), eventos, commands.
- [ ] `.specs/codebase/STRUCTURE.md`: nova organizacao de pastas.
- [ ] `.specs/codebase/TESTING.md`: estrategia de integracao + unit, fixtures docker-compose.
- [ ] `.specs/codebase/INTEGRATIONS.md`: MassTransit + SQS + EF Outbox + OTel.
- [ ] `.specs/codebase/CONCERNS.md`: riscos pos-migracao e decisoes em aberto.

### Criterio de DONE
7 arquivos criados + referenciados no `PROJECT.md`.

---

## Code review por fase — roteiro padrao

Ao final de cada fase, antes de marcar `[x]` e atualizar `STATE.md`:

1. **Build:** `dotnet build` — 0 erros, 0 warnings.
2. **Format:** `dotnet format --verify-no-changes` (opcional mas recomendado).
3. **Grep de residuos:** para cada item removido na fase, grep case-insensitive por nomes antigos. Zero matches esperados.
4. **Diff:** revisar o diff inteiro. Nenhuma mudanca fora do escopo da fase.
5. **Testes:** `dotnet test tests/IntegrationTests/` — todos verdes.
6. **Smoke docker:** `docker compose down -v && docker compose up --build -d && curl happy path` — saga completa.
7. **Logs:** nenhum ERROR nos logs do happy path.
8. **Sugerir commit:** mensagem em conventional commit pt-BR (seguir CLAUDE.md).

---

## Criterios globais de "DONE" do M10

- [ ] Zero referencias a `SagaOrchestrator` / `saga-orchestrator` no repo.
- [ ] 4 containers .NET no docker-compose.
- [ ] 0 usos de `IdempotencyStore`, `SqsConfig`, `SqsTracePropagation`, `InMemoryOutbox`.
- [ ] Migrations EF formais no OrderService.
- [ ] Outbox EF Postgres em producao.
- [ ] `ConcurrencyMode.Optimistic` + `RowVersion`.
- [ ] 7+5=12 cenarios de integracao verdes.
- [ ] README e docs 01-10 refletem nova arquitetura.
- [ ] `.specs/codebase/*` existente e completo.
- [ ] `ROADMAP.md` atualizado com todas as features como DONE e arquivadas.
- [ ] `STATE.md` com decision final do M10.

---

## Protocolo de retomada entre sessoes

Quando uma sessao futura do Claude abrir este plano:

1. **Ler este arquivo inteiro primeiro.**
2. Ler `STATE.md` para confirmar a ultima fase concluida.
3. Rodar `dotnet build` e `docker compose ps` para validar o estado atual.
4. Identificar a proxima fase nao marcada `[x]`.
5. Executar os checklists da fase na ordem.
6. Ao terminar, atualizar este arquivo (marcar `[x]`) + atualizar `STATE.md` + sugerir commit.
7. Sugerir o proximo prompt para o usuario continuar.

**Nunca pular fases.** Se algo bloquear, documentar em `Blockers` do `STATE.md` e pausar.
