# State

## Decisions

- **2026-03-28:** Projeto inicializado com spec-driven workflow. Milestones M1-M4 definidos seguindo o PROMPT-INIT.md.
- **2026-03-28:** Feature infra-docker-compose implementada (T1-T6). Docker Compose, LocalStack, PostgreSQL, 5 servicos .NET placeholder com health checks.
- **2026-03-28:** "Multiplas sagas concorrentes" movido de out-of-scope para M5 com exemplo didatico. Adicionado doc sobre concorrencia no M4 (docs-didaticos).
- **2026-03-28:** Feature project-skeleton implementada (T1-T8). Solution .NET com 6 projetos, Directory.Build.props, global.json, projeto Shared com contracts/replies/SqsConfig, Worker Services, smoke tests de conectividade SQS+PostgreSQL. M1 concluido.
- **2026-03-28:** Feature saga-state-machine implementada (T1-T7). EF Core + Npgsql no SagaOrchestrator, modelo SagaInstance/SagaStateTransition, SagaStateMachine com transicoes, Worker com polling de replies, endpoints POST/GET /sagas.
- **2026-03-28:** Feature command-reply-flow implementada (T1-T3). Workers do PaymentService, InventoryService e ShippingService fazem polling de comandos, simulam processamento e enviam replies com Success=true. Correlation via SagaId preservado.
- **2026-03-28:** Feature order-api implementada (T1-T6). OrderService com EF Core + Npgsql, modelo Order, POST /orders (cria pedido + inicia saga via HTTP), GET /orders/{id} (retorna pedido + estado da saga). M2 concluido.
- **2026-03-28:** Feature compensation-cascade implementada. Estados de compensacao (ShippingCancelling, InventoryReleasing, PaymentRefunding, Failed) na maquina de estados. Comandos de compensacao (RefundPayment, ReleaseInventory, CancelShipping) e replies correspondentes. Worker do orquestrador processa falhas e cascata de compensacao. Service workers despacham comandos forward e de compensacao via message attribute CommandType. Simulacao de falha via header X-Simulate-Failure propagado do OrderService ao orquestrador e aos comandos SQS. CompensationDataJson na SagaInstance armazena metadados de steps completos para uso nos comandos de compensacao.
- **2026-03-28:** Feature idempotency implementada. IdempotencyStore centralizado em Shared/Idempotency com Npgsql direto (sem EF Core). Tabela idempotency_keys (idempotency_key PK, saga_id, result_json, created_at) criada automaticamente via EnsureTableAsync. Aplicada nos 3 service workers (Payment, Inventory, Shipping) para comandos forward E de compensacao. Cada handler verifica a chave antes de processar — se ja processado, reenvia o reply anterior sem reprocessar.

- **2026-03-28:** Feature dlq-visibility implementada. Endpoint GET /dlq no SagaOrchestrator lista mensagens de todas as 8 DLQs (peek com VisibilityTimeout=0). Endpoint POST /dlq/redrive reenvia mensagem da DLQ para a fila original e deleta da DLQ. SqsConfig ampliado com AllDlqNames (array das 8 DLQs) e DlqToOriginalQueue (mapeamento DLQ->fila original). M3 concluido.

- **2026-03-29:** Feature otel-traces implementada. OpenTelemetry SDK 1.15.0 integrado em todos os 5 servicos via Shared. Trace propagation via W3C TraceContext em SQS message attributes (SqsTracePropagation helper). SagaActivitySource com factory methods para spans padronizados (send/process command/reply). Console exporter como default, OTLP configuravel via env var. Instrumentacao automatica AspNetCore + HttpClient. Pacotes Microsoft.Extensions atualizados de preview para 10.0.0 estavel (compatibilidade com OTel 1.15.0). Decisao: sem instrumentacao de DB (EF Core/Npgsql) — complexidade sem valor didatico.

- **2026-03-31:** Feature resource-locking implementada. InventoryRepository com Npgsql direto: tabelas inventory (product_id, name, quantity) e inventory_reservations (reservation_id, product_id, quantity, saga_id). TryReserveAsync com SELECT FOR UPDATE (useLock=true) ou sem lock + delay 150ms (useLock=false) para expor TOCTOU. ReleaseAsync para compensacao. Worker atualizado para processar mensagens em paralelo (Task.WhenAll) — critico para demo de race condition. Endpoints /inventory/stock e /inventory/reset adicionados. INVENTORY_LOCKING_ENABLED env var no docker-compose.yml.

- **2026-03-31:** Feature concurrent-saga-demo implementada. Script bash scripts/concurrent-saga-demo.sh com opcoes --no-lock, --pedidos N, --estoque N. Reseta estoque, dispara pedidos paralelos, poll de sagas e diagnostico de overbooking. docs/07-concorrencia-sagas.md reescrito com implementacao real, schema SQL, logs esperados e instrucoes de execucao. M5 concluido.

## Blockers

_Nenhum no momento._

## Lessons Learned

- **LocalStack >=2026.3:** Requer `LOCALSTACK_ACKNOWLEDGE_ACCOUNT_REQUIREMENT=1` para rodar sem auth token (snooze temporario)
- **.NET 10 preview aspnet image:** Nao inclui `curl` nem `wget`. Necessario instalar `curl` via apt no Dockerfile para health checks do Docker Compose
- **.NET 10 csproj:** Precisa de `<ImplicitUsings>enable</ImplicitUsings>` para `WebApplication` funcionar sem `using` explicito
- **AWSSDK.SQS v4 preview:** Versao 4.0.0-preview.5 e necessaria para .NET 10 — versao 3.x nao e compativel
- **EF Core 10 preview:** Versao exata 10.0.0-preview.3.25171.1 nao existe no NuGet — usar 10.0.0-preview.3.25171.6. Npgsql.EFCore 10.0.0-preview.3 e compativel
- **Dockerfile com Shared:** Build context precisa ser raiz do repo (nao src/Service) para copiar Directory.Build.props e Shared
- **OpenTelemetry 1.15.0 + .NET 10:** OTel 1.15.0 depende de Microsoft.Extensions.*.Abstractions 10.0.0 (estavel). Versoes preview (10.0.0-preview.3) causam NU1605 (downgrade detectado). Atualizar para 10.0.0 estavel resolve
- **SQS trace propagation manual:** AWSSDK.SQS nao tem instrumentacao OTel nativa. Necessario inject/extract manual de traceparent/tracestate via MessageAttributes usando Propagators.DefaultTextMapPropagator

## Lessons Learned (M5)

- **Task.WhenAll no SQS worker:** Para expor race conditions realmente, o worker precisa processar mensagens do mesmo batch em paralelo. O foreach sequencial nunca gera concorrencia real, mesmo sem lock. Task.WhenAll com mensagens do mesmo ReceiveMessage batch e a abordagem correta.
- **Delay artificial como janela TOCTOU:** Adicionar Task.Delay(150ms) entre SELECT e UPDATE (modo sem lock) garante que multiplas transacoes paralelas vejam o mesmo valor antes de qualquer UPDATE. Sem o delay, a velocidade do Npgsql pode serializar naturalmente mesmo sem FOR UPDATE.
- **inventory_reservations como ponte de compensacao:** Armazenar reservation_id na tabela inventory_reservations com referencia ao product_id e qty permite que a compensacao (ReleaseInventory) restaure exatamente o estoque correto sem depender de logica no orquestrador.

## Deferred Ideas

_Ver "Future Considerations" no ROADMAP.md._

## Preferences

- Documentacao em portugues
- Construcao incremental por milestones
- Usar subagentes para trabalho pesado, manter janela de tokens baixa
