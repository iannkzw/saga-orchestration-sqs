# Roadmap

**Current Milestone:** M5 - Concorrencia entre Sagas
**Status:** DONE

**M4 - Observabilidade e Documentacao:** DONE

---

## M1 - Infraestrutura Base

**Goal:** Todos os servicos sobem com `docker compose up`, filas SQS criadas no LocalStack, PostgreSQL acessivel, health checks passando.
**Target:** Infraestrutura funcional e reproduzivel

### Features

**infra-docker-compose** - DONE

- Docker Compose unificado com LocalStack, PostgreSQL e 5 servicos .NET
- Init scripts para criacao automatica das filas SQS
- Init SQL para schemas do PostgreSQL
- Health checks em todos os containers

**project-skeleton** - DONE

- Solution .NET com 5 projetos (OrderService, SagaOrchestrator, PaymentService, InventoryService, ShippingService) + Shared
- Directory.Build.props e global.json configurados para .NET 10
- Projeto Shared com contracts (commands/replies), SQS helpers e modelo de IdempotencyKey
- Minimal API no OrderService, Worker Services nos demais
- Cada servico conecta ao SQS e PostgreSQL (smoke test de conectividade)

---

## M2 - Saga Happy Path

**Goal:** Fluxo completo Order -> Payment -> Inventory -> Shipping funciona end-to-end sem falhas. POST no OrderService inicia saga, orquestrador coordena todos os passos, saga termina em estado Completed.
**Target:** Happy path demonstravel via curl/script

### Features

**saga-state-machine** - DONE

- Modelo de persistencia da saga (SagaId, estado atual, historico de transicoes)
- Maquina de estados no SagaOrchestrator (Pending -> PaymentProcessing -> InventoryReserving -> ShippingScheduling -> Completed)
- Transicoes acionadas por replies dos servicos via SQS
- EF Core + Npgsql com SagaDbContext, EnsureCreated no startup
- Endpoints POST /sagas e GET /sagas/{id} para teste manual

**command-reply-flow** - DONE

- Orquestrador envia comandos tipados (ProcessPayment, ReserveInventory, ScheduleShipping) via filas SQS dedicadas
- Cada servico processa o comando e envia reply (Success/Failure) para fila de resposta do orquestrador
- Correlation via SagaId em todas as mensagens

**order-api** - DONE

- Endpoint POST /orders que cria pedido e inicia saga via HTTP ao orquestrador
- Endpoint GET /orders/{id} que retorna estado atual do pedido e da saga
- Persistencia do pedido no PostgreSQL com EF Core + Npgsql

---

## M3 - Compensacoes e Resiliencia — DONE

**Goal:** Quando um passo falha, o orquestrador executa compensacoes na ordem reversa. Mensagens problematicas vao para DLQ. Handlers sao idempotentes.
**Target:** Cenarios de falha demonstraveis e reproduziveis

### Features

**compensation-cascade** - DONE

- Estados de compensacao: ShippingCancelling, InventoryReleasing, PaymentRefunding, Failed
- Compensacoes na ordem reversa baseada no ponto de falha
- Comandos: RefundPayment, ReleaseInventory, CancelShipping com replies correspondentes
- CompensationDataJson armazena metadados (TransactionId, ReservationId, TrackingNumber)
- Simulacao de falha via header X-Simulate-Failure (payment|inventory|shipping)
- Message attribute CommandType para despacho de comandos forward vs compensacao

**idempotency** - DONE

- IdempotencyKey em todos os comandos (BaseCommand)
- IdempotencyStore centralizado em Shared/Idempotency (Npgsql direto, tabela idempotency_keys)
- Handlers nos 3 servicos (Payment, Inventory, Shipping) verificam chave antes de processar
- Se ja processado, retorna resultado anterior sem reprocessar
- Aplicado em comandos forward (ProcessPayment, ReserveInventory, ScheduleShipping) E de compensacao (RefundPayment, ReleaseInventory, CancelShipping)

**dlq-visibility** - DONE

- Dead Letter Queue configurada para cada fila SQS (maxReceiveCount=3)
- Endpoint GET /dlq no SagaOrchestrator lista mensagens de todas as 8 DLQs
- Cada mensagem inclui: queue name, body (JSON), ApproximateReceiveCount, SentTimestamp
- Endpoint POST /dlq/redrive reenvia mensagem da DLQ para a fila original
- SqsConfig com AllDlqNames e DlqToOriginalQueue para mapeamento centralizado

---

## M4 - Observabilidade e Documentacao

**Goal:** Traces distribuidos conectam toda a saga. Documentacao didatica completa em portugues cobre todos os padroes demonstrados.
**Target:** PoC completa, documentada e pronta para publicacao

### Features

**otel-traces** - DONE

- OpenTelemetry SDK integrado em todos os servicos (v1.15.0)
- Trace propagation via SQS message attributes (W3C TraceContext)
- Console exporter como default, OTLP configuravel via OTEL_EXPORTER_OTLP_ENDPOINT
- SagaId como tag/attribute nos spans (saga.id, saga.command_type, saga.state)
- Instrumentacao automatica HTTP (AspNetCore + HttpClient)
- ActivitySource compartilhada "SagaOrchestration" com factory methods padronizados

**docs-didaticos** - DONE

- docs/01-fundamentos-sagas.md: Saga vs 2PC, orquestrada vs coreografada, justificativa da escolha
- docs/02-maquina-de-estados.md: Diagrama completo, estados forward/compensacao/terminal, implementacao pura estatica
- docs/03-padroes-compensacao.md: Cascata reversa, CompensationDataJson, HandleFailureAsync/HandleCompensationReplyAsync
- docs/04-idempotencia-retry.md: IdempotencyStore (Npgsql), chaves por saga, fluxo com idempotency hit, visibility timeout
- docs/05-sqs-dlq-visibility.md: Topologia de filas, RedrivePolicy, GET /dlq e POST /dlq/redrive com exemplos
- docs/06-opentelemetry-traces.md: W3C TraceContext sobre SQS, SagaActivitySource, AddSagaTracing, exporters
- docs/07-concorrencia-sagas.md: Race conditions, pessimistic vs optimistic locking, comparativo, estrategias (teorico/M5)
- docs/08-guia-pratico.md: Passo a passo completo para todos os cenarios com curl, troubleshooting

**readme-walkthrough** - DONE

- README.md com walkthrough canonico da demo
- Instrucoes de setup (pre-requisitos, docker compose up)
- Exemplos de curl para happy path, falha/compensacao, DLQ visibility
- Script automatizado (happy-path-demo.sh) e demo de concorrencia
- Estrutura de diretorios comentada, tabela de portas, links para 8 docs

---

## M5 - Concorrencia entre Sagas

**Goal:** Demonstrar como lidar com multiplas sagas concorrentes que disputam o mesmo recurso (ex: mesmo produto no inventario). Exemplo pratico de pessimistic locking no PostgreSQL para evitar race conditions.
**Target:** Cenario reproduzivel onde 2+ sagas concorrentes sobre o mesmo recurso sao tratadas corretamente

### Features

**resource-locking** - DONE

- InventoryRepository com Npgsql direto: tabelas inventory + inventory_reservations
- SELECT FOR UPDATE (useLock=true) vs sem lock (useLock=false) controlado por INVENTORY_LOCKING_ENABLED
- Worker processa mensagens em paralelo (Task.WhenAll) para expor race conditions reais
- Endpoints GET /inventory/stock/{productId} e POST /inventory/reset para demos
- INVENTORY_LOCKING_ENABLED env var no docker-compose.yml (default: true)

**concurrent-saga-demo** - DONE

- Script bash scripts/concurrent-saga-demo.sh com opcoes --no-lock, --pedidos N, --estoque N
- Reset de estoque + 5 pedidos paralelos + polling de estado das sagas
- Cenario: estoque=2, 5 pedidos → 2 Completed + 3 Failed com compensacao (COM lock)
- docs/07-concorrencia-sagas.md atualizado com implementacao real, logs e saidas esperadas

**demo-scripts** - DONE

- `scripts/lib/common.sh`: funcoes compartilhadas (check_health, poll_saga, get_transitions, get_stock, reset_stock)
- `scripts/concurrent-saga-demo.sh` reescrito: corrige loop duplo (Bug 1), deteccao real de lock via docker logs (Bug 2), remove dead code RAW_RESPONSES (Bug 3)
- `scripts/happy-path-demo.sh`: 4 cenarios sequenciais com verificacoes de transicoes e estoque

---

---

## M6 - Optimistic Locking (extensao do M5)

**Goal:** Demonstrar locking otimista como alternativa ao pessimistic locking com comportamento, throughput e complexidade diferentes sob concorrencia real.
**Status:** DONE

### Features

**optimistic-locking** - DONE

- Coluna `version INTEGER NOT NULL DEFAULT 0` na tabela `inventory` (ADD COLUMN IF NOT EXISTS)
- `TryReserveOptimisticAsync`: SELECT sem lock + UPDATE WHERE version = @expected + retry automatico
- `INVENTORY_LOCKING_MODE` (pessimistic|optimistic|none) substitui `INVENTORY_LOCKING_ENABLED` (mantida como fallback)
- `INVENTORY_OPTIMISTIC_MAX_RETRIES` configuravel (default: 3)
- Worker atualizado com switch expression para despachar ao metodo correto
- `ResetStockAsync` zera `version=0` para demos reproduziveis
- docs/07-concorrencia-sagas.md: secao de implementacao real do modo otimista com logs esperados

---

## M7 - Testes de Integração

**Goal:** Suite automatizada de testes que valida todos os comportamentos implementados nos M1–M5 sem depender de execução manual com curl.
**Status:** DONE

### Features

**integration-tests** - DONE

- Projeto xUnit `tests/IntegrationTests/` adicionado à solution
- `DockerComposeFixture`: sobe/derruba Docker Compose via Process com polling de health checks em todos os 5 serviços
- `SagaClient`: encapsula `POST /orders` e `GET /sagas/{id}` com polling `WaitForTerminalStateAsync` (timeout 30s)
- `InventoryClient`: encapsula `GET /inventory/stock` e `POST /inventory/reset`
- 7 cenários implementados: happy path, 3 compensações (payment/inventory/shipping), isolamento/idempotência, concorrência com lock, concorrência documentacional
- `docker-compose.test.yml`: override com `INVENTORY_LOCKING_MODE=pessimistic` para testes determinísticos
- Comando: `dotnet test tests/IntegrationTests/`

---

## Future Considerations

- Saga coreografada como PoC complementar para comparacao
- Dashboard Grafana para visualizar estado das sagas
- Testes de carga para demonstrar comportamento sob concorrencia
- Timeout e retry policies configuráveis no orquestrador
- Versionamento de comandos/eventos (schema evolution)
