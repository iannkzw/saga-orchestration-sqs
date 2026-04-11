# State Archive — Decisions M1–M8

> Decisions de milestones concluídas movidas do STATE.md para reduzir contexto de trabalho.

## Decisions (M1 — 2026-03-28)

- Projeto inicializado com spec-driven workflow. Milestones M1-M4 definidos seguindo o PROMPT-INIT.md.
- Feature infra-docker-compose implementada (T1-T6). Docker Compose, LocalStack, PostgreSQL, 5 servicos .NET placeholder com health checks.
- "Multiplas sagas concorrentes" movido de out-of-scope para M5 com exemplo didatico. Adicionado doc sobre concorrencia no M4 (docs-didaticos).
- Feature project-skeleton implementada (T1-T8). Solution .NET com 6 projetos, Directory.Build.props, global.json, projeto Shared com contracts/replies/SqsConfig, Worker Services, smoke tests de conectividade SQS+PostgreSQL. M1 concluido.

## Decisions (M2 — 2026-03-28)

- Feature saga-state-machine implementada (T1-T7). EF Core + Npgsql no SagaOrchestrator, modelo SagaInstance/SagaStateTransition, SagaStateMachine com transicoes, Worker com polling de replies, endpoints POST/GET /sagas.
- Feature command-reply-flow implementada (T1-T3). Workers do PaymentService, InventoryService e ShippingService fazem polling de comandos, simulam processamento e enviam replies com Success=true. Correlation via SagaId preservado.
- Feature order-api implementada (T1-T6). OrderService com EF Core + Npgsql, modelo Order, POST /orders (cria pedido + inicia saga via HTTP), GET /orders/{id} (retorna pedido + estado da saga). M2 concluido.

## Decisions (M3 — 2026-03-28)

- Feature compensation-cascade implementada. Estados de compensacao (ShippingCancelling, InventoryReleasing, PaymentRefunding, Failed) na maquina de estados. Comandos de compensacao (RefundPayment, ReleaseInventory, CancelShipping) e replies correspondentes. Worker do orquestrador processa falhas e cascata de compensacao. Service workers despacham comandos forward e de compensacao via message attribute CommandType. Simulacao de falha via header X-Simulate-Failure propagado do OrderService ao orquestrador e aos comandos SQS. CompensationDataJson na SagaInstance armazena metadados de steps completos para uso nos comandos de compensacao.
- Feature idempotency implementada. IdempotencyStore centralizado em Shared/Idempotency com Npgsql direto (sem EF Core). Tabela idempotency_keys (idempotency_key PK, saga_id, result_json, created_at) criada automaticamente via EnsureTableAsync. Aplicada nos 3 service workers (Payment, Inventory, Shipping) para comandos forward E de compensacao. Cada handler verifica a chave antes de processar — se ja processado, reenvia o reply anterior sem reprocessar.
- Feature dlq-visibility implementada. Endpoint GET /dlq no SagaOrchestrator lista mensagens de todas as 8 DLQs (peek com VisibilityTimeout=0). Endpoint POST /dlq/redrive reenvia mensagem da DLQ para a fila original e deleta da DLQ. SqsConfig ampliado com AllDlqNames (array das 8 DLQs) e DlqToOriginalQueue (mapeamento DLQ->fila original). M3 concluido.

## Decisions (M4 — 2026-03-29)

- Feature otel-traces implementada. OpenTelemetry SDK 1.15.0 integrado em todos os 5 servicos via Shared. Trace propagation via W3C TraceContext em SQS message attributes (SqsTracePropagation helper). SagaActivitySource com factory methods para spans padronizados (send/process command/reply). Console exporter como default, OTLP configuravel via env var. Instrumentacao automatica AspNetCore + HttpClient. Pacotes Microsoft.Extensions atualizados de preview para 10.0.0 estavel (compatibilidade com OTel 1.15.0). Decisao: sem instrumentacao de DB (EF Core/Npgsql) — complexidade sem valor didatico.

## Decisions (M5 — 2026-03-31)

- Feature resource-locking implementada. InventoryRepository com Npgsql direto: tabelas inventory (product_id, name, quantity) e inventory_reservations (reservation_id, product_id, quantity, saga_id). TryReserveAsync com SELECT FOR UPDATE (useLock=true) ou sem lock + delay 150ms (useLock=false) para expor TOCTOU. ReleaseAsync para compensacao. Worker atualizado para processar mensagens em paralelo (Task.WhenAll) — critico para demo de race condition. Endpoints /inventory/stock e /inventory/reset adicionados. INVENTORY_LOCKING_ENABLED env var no docker-compose.yml.
- Feature concurrent-saga-demo implementada. Script bash scripts/concurrent-saga-demo.sh com opcoes --no-lock, --pedidos N, --estoque N. Reseta estoque, dispara pedidos paralelos, poll de sagas e diagnostico de overbooking. docs/07-concorrencia-sagas.md reescrito com implementacao real, schema SQL, logs esperados e instrucoes de execucao. M5 concluido.

## Decisions (M5/M6 — 2026-04-01 a 2026-04-02)

- Feature demo-scripts implementada. `scripts/concurrent-saga-demo.sh` reescrito corrigindo 3 bugs (loop duplo, --no-lock sem efeito, RAW_RESPONSES dead code). `scripts/happy-path-demo.sh` criado com 4 cenarios sequenciais (happy path, falha pagamento, falha inventario, falha shipping). Funcoes compartilhadas extraidas para `scripts/lib/common.sh` (check_health, poll_saga, get_transitions, get_stock, reset_stock).
- Feature readme-walkthrough implementada. README.md criado na raiz com walkthrough completo. Todos os milestones M1–M5 + README concluídos.
- Feature optimistic-locking implementada. Coluna `version INTEGER NOT NULL DEFAULT 0` adicionada via `ALTER TABLE IF NOT EXISTS` no `EnsureTablesAsync`. `TryReserveOptimisticAsync` com loop de retry. Env var `INVENTORY_LOCKING_MODE` (pessimistic|optimistic|none) substitui `INVENTORY_LOCKING_ENABLED`. Worker atualizado com switch expression.

## Decisions (M7 — 2026-04-02)

- Feature integration-tests implementada. Projeto xUnit `tests/IntegrationTests/` adicionado à solution. DockerComposeFixture sobe/derruba o compose via Process com polling de health checks. SagaClient e InventoryClient encapsulam os endpoints HTTP. 7 cenários cobertos. Build: 0 erros, 0 warnings. Decisao tecnica: await de tuples nao suportado em .NET 10 preview — usar Task.WhenAll com array. JsonElement nao e nullable — usar response.Content.ReadFromJsonAsync diretamente.

## Decisions (M8 — 2026-04-03)

- Feature code-review concluída. Revisão estática de toda a implementação M1–M5 sem execução de testes. 16 findings documentados (0 CRITICAL, 2 HIGH, 6 MEDIUM, 4 LOW, 4 INFO). Relatório em `.specs/features/code-review/REPORT.md`. Principais riscos: [HIGH-01] grep case-sensitive quebra `--no-lock`; [HIGH-02] dual-write não-atômico no SagaOrchestrator.
- Feature otel-lgtm implementada (M8). Stack LGTM adicionada ao Docker Compose. OTel Collector configurado com tail sampling. AddSagaLogging() criado. Grafana provisionado com datasources Tempo+Loki e dashboard. Build: 0 erros, 0 warnings.
