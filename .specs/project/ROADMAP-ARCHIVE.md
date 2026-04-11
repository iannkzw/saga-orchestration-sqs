# Roadmap Archive — Milestones Concluídas

> Milestones M1–M8 (DONE) e M9 (OBSOLETE) movidas do ROADMAP.md para reduzir contexto de trabalho.

---

## M1 - Infraestrutura Base — DONE

**Goal:** Todos os servicos sobem com `docker compose up`, filas SQS criadas no LocalStack, PostgreSQL acessivel, health checks passando.

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

## M2 - Saga Happy Path — DONE

**Goal:** Fluxo completo Order -> Payment -> Inventory -> Shipping funciona end-to-end sem falhas.

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
- Aplicado em comandos forward E de compensacao

**dlq-visibility** - DONE

- Dead Letter Queue configurada para cada fila SQS (maxReceiveCount=3)
- Endpoint GET /dlq no SagaOrchestrator lista mensagens de todas as 8 DLQs
- Endpoint POST /dlq/redrive reenvia mensagem da DLQ para a fila original
- SqsConfig com AllDlqNames e DlqToOriginalQueue para mapeamento centralizado

---

## M4 - Observabilidade e Documentacao — DONE

**Goal:** Traces distribuidos conectam toda a saga. Documentacao didatica completa em portugues cobre todos os padroes demonstrados.

### Features

**otel-traces** - DONE

- OpenTelemetry SDK integrado em todos os servicos (v1.15.0)
- Trace propagation via SQS message attributes (W3C TraceContext)
- Console exporter como default, OTLP configuravel via OTEL_EXPORTER_OTLP_ENDPOINT
- SagaId como tag/attribute nos spans
- Instrumentacao automatica HTTP (AspNetCore + HttpClient)
- ActivitySource compartilhada "SagaOrchestration" com factory methods padronizados

**docs-didaticos** - DONE

- 8 docs didaticos em portugues (fundamentos, state machine, compensacao, idempotencia, DLQ, OTel, concorrencia, guia pratico)

**readme-walkthrough** - DONE

- README.md com walkthrough canonico, setup, exemplos curl, scripts, estrutura de diretorios

---

## M5 - Concorrencia entre Sagas — DONE

**Goal:** Demonstrar multiplas sagas concorrentes disputando o mesmo recurso com pessimistic locking.

### Features

**resource-locking** - DONE

- InventoryRepository com Npgsql direto: tabelas inventory + inventory_reservations
- SELECT FOR UPDATE vs sem lock controlado por INVENTORY_LOCKING_ENABLED
- Worker processa mensagens em paralelo (Task.WhenAll)
- Endpoints GET /inventory/stock/{productId} e POST /inventory/reset

**concurrent-saga-demo** - DONE

- Script concurrent-saga-demo.sh com opcoes --no-lock, --pedidos N, --estoque N
- docs/07-concorrencia-sagas.md atualizado com implementacao real

**demo-scripts** - DONE

- scripts/lib/common.sh com funcoes compartilhadas
- scripts/happy-path-demo.sh com 4 cenarios sequenciais

---

## M6 - Optimistic Locking — DONE

**Goal:** Demonstrar locking otimista como alternativa ao pessimistic locking.

### Features

**optimistic-locking** - DONE

- Coluna version na tabela inventory + TryReserveOptimisticAsync com retry
- INVENTORY_LOCKING_MODE (pessimistic|optimistic|none)
- INVENTORY_OPTIMISTIC_MAX_RETRIES configuravel (default: 3)

---

## M7 - Testes de Integração — DONE

**Goal:** Suite automatizada de testes que valida todos os comportamentos M1–M5.

### Features

**integration-tests** - DONE

- Projeto xUnit tests/IntegrationTests/ com DockerComposeFixture
- SagaClient e InventoryClient encapsulam endpoints HTTP
- 7 cenarios: happy path, 3 compensacoes, isolamento/idempotencia, concorrencia com lock, concorrencia documentacional
- docker-compose.test.yml com INVENTORY_LOCKING_MODE=pessimistic

---

## M8 - Observabilidade LGTM — DONE

**Goal:** Traces distribuidos visiveis no Grafana/Tempo e logs estruturados no Grafana/Loki.

### Features

**otel-lgtm** - DONE

- Stack LGTM (Grafana + Tempo + Loki) no Docker Compose
- OTel Collector com tail sampling (drop /health, keep errors)
- Exportacao de logs via OpenTelemetry (ILogger -> OTLP gRPC -> Collector -> Loki)
- Dashboard "Saga Orchestration - Overview" provisionado no Grafana

---

## M9 - Sincronização de Status do Pedido — OBSOLETE

**Substituido pelo M10** (fusao OrderService + SagaOrchestrator). Com saga e order no mesmo servico/DB, a state machine atualiza Order.Status diretamente nas transicoes terminais.

### Features

**order-status-sync** - OBSOLETE (resolvido pelo M10)
