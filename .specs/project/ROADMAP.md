# Roadmap

**Current Milestone:** M4 - Observabilidade e Documentacao
**Status:** Planned

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

**otel-traces** - PLANNED

- OpenTelemetry SDK integrado em todos os servicos
- Trace propagation via SQS message attributes
- Exporter configurado (OTLP ou console, minimo viavel)
- SagaId como tag/attribute nos spans

**docs-didaticos** - PLANNED

- Fundamentos de Sagas (Saga vs 2PC, orquestrada vs coreografada)
- Maquina de Estados da Saga (diagrama + explicacao)
- Padroes de Compensacao
- Idempotencia e Retry
- SQS, DLQ e Visibility Timeout
- Concorrencia entre Sagas (race conditions, pessimistic vs optimistic locking, estrategias de resolucao)
- Guia Pratico (passo a passo para reproduzir e testar cenarios)

**readme-walkthrough** - PLANNED

- README.md com walkthrough canonico da demo
- Instrucoes de setup (pre-requisitos, docker compose up)
- Exemplos de curl para testar happy path e cenarios de falha
- Screenshots/logs dos resultados esperados

---

## M5 - Concorrencia entre Sagas

**Goal:** Demonstrar como lidar com multiplas sagas concorrentes que disputam o mesmo recurso (ex: mesmo produto no inventario). Exemplo pratico de pessimistic locking no PostgreSQL para evitar race conditions.
**Target:** Cenario reproduzivel onde 2+ sagas concorrentes sobre o mesmo recurso sao tratadas corretamente

### Features

**resource-locking** - PLANNED

- Pessimistic lock (SELECT ... FOR UPDATE) no InventoryService ao reservar estoque
- Demonstracao de race condition sem lock (antes) vs com lock (depois)
- Teste com 2+ pedidos simultaneos sobre o mesmo produto
- Documentacao didatica explicando as estrategias (pessimistic vs optimistic locking, idempotency como complemento)

**concurrent-saga-demo** - PLANNED

- Script de teste que dispara N pedidos concorrentes via curl/script
- Logs mostrando a ordem de execucao e resolucao de conflitos
- Cenario onde estoque e insuficiente para todos — compensacoes parciais

---

## Future Considerations

- Saga coreografada como PoC complementar para comparacao
- Dashboard Grafana para visualizar estado das sagas
- Testes de carga para demonstrar comportamento sob concorrencia
- Timeout e retry policies configuráveis no orquestrador
- Versionamento de comandos/eventos (schema evolution)
- Optimistic concurrency como alternativa ao pessimistic locking (version column)
