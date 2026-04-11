# Roadmap

**Current Milestone:** M10 - Migração MassTransit
**Status:** Planning

> Milestones concluídas (M1–M8 DONE, M9 OBSOLETE) arquivadas em [ROADMAP-ARCHIVE.md](ROADMAP-ARCHIVE.md)

---

## M10 - Migração MassTransit

**Goal:** Migrar a saga orquestrada manual (SQS polling + state machine imperativa + idempotência manual) para MassTransit declarativo. Fundir OrderService + SagaOrchestrator em um unico OrderService (API + state machine). Eliminar ~900+ linhas, 9 filas SQS, dual-write, e bug de Order.Status preso em Processing (M9 obsoleto). Documentação didática reescrita para os novos conceitos.
**Constraint:** Preservar arquitetura de microsserviços com 4 containers independentes (OrderService, PaymentService, InventoryService, ShippingService), cada um com seu proprio Program.cs e Dockerfile. Consumers ficam no servico correspondente, state machine fica no OrderService.
**Status:** Planning

### Features

**mt-merge-order-orchestrator** - DONE

- Fundir SagaOrchestrator no OrderService (mesmo bounded context: order fulfillment)
- Justificativa: com MassTransit, a state machine e declarativa (~1 classe) — nao justifica um servico dedicado
- Mover SagaDbContext, OrderSagaState e OrderStateMachine para dentro do OrderService
- Unificar OrderDbContext e SagaDbContext em um unico DbContext (Orders + SagaState na mesma DB/transacao)
- Eliminar HTTP hop OrderService → SagaOrchestrator (POST /sagas): OrderService publica OrderCreated direto no bus
- Absorver endpoints GET /sagas/{id} no OrderService (ou expor via GET /orders/{id} com saga embutida, como ja faz hoje)
- Resolver bug Order.Status preso em Processing: state machine atualiza Order.Status diretamente nas transicoes terminais (Completed/Failed) — torna M9 obsoleto
- Remover projeto SagaOrchestrator, Dockerfile e container do docker-compose
- Remover Worker.cs do OrderService (polling de order-status-updates) — nao mais necessario
- Remover fila SQS order-status-updates e sua DLQ
- De 5 para 4 containers .NET no docker-compose

**mt-state-machine** - PLANNED

- Criar `OrderSagaState : SagaStateMachineInstance` com propriedades tipadas (PaymentTransactionId, InventoryReservationId, ShippingTrackingNumber, RowVersion)
- Criar `OrderStateMachine : MassTransitStateMachine<OrderSagaState>` com blocos Initially/During/When declarativos
- State machine e saga state ficam no projeto OrderService (unico container com API + saga)
- Happy path: Pending → PaymentProcessing → InventoryReserving → ShippingScheduling → Completed
- Compensation chain: ShippingCancelling → InventoryReleasing → PaymentRefunding → Failed
- Transicoes terminais (Completed/Failed) atualizam Order.Status no mesmo DbContext
- SetCompletedWhenFinalized() para cleanup automatico
- Remover SagaStateMachine.cs imperativo, enums SagaState, dicionarios estaticos de transicao

**mt-event-contracts** - PLANNED

- Criar contratos de evento em Shared/Contracts/Events/ (PaymentCompleted, PaymentFailed, InventoryReserved, InventoryReservationFailed, ShippingScheduled, ShippingFailed, etc.)
- Criar contrato OrderCreated para iniciar a saga (substitui HTTP POST /sagas)
- Substituir Reply types (ProcessPaymentReply, etc.) por eventos tipados
- CorrelateById(ctx => ctx.Message.SagaId) em cada evento na state machine
- Remover reply contracts antigos e SagaTerminatedNotification (M9 obsoleto)

**mt-consumers** - PLANNED

- Cada consumer fica no projeto/container do seu respectivo microsservico (preserva deploy independente):
  - PaymentService: ProcessPaymentConsumer, RefundPaymentConsumer
  - InventoryService: ReserveInventoryConsumer, ReleaseInventoryConsumer
  - ShippingService: ScheduleShippingConsumer, CancelShippingConsumer
- Consumers publicam eventos (context.Publish<TEvent>) em vez de enviar para reply queues
- Remover Worker.cs com polling loop manual dos 3 servicos (Payment, Inventory, Shipping)
- Remover despacho manual por MessageAttribute CommandType

**mt-messaging-infra** - PLANNED

- Configurar MassTransit UsingAmazonSqs com ConfigureEndpoints(context) no Program.cs de cada servico (4 Program.cs independentes)
- Cada microsservico registra apenas seus proprios consumers no AddMassTransit (isolamento de deploy)
- OrderService registra state machine + saga repository; servicos registram seus consumers
- Criacao automatica de filas pelo MassTransit (eliminar init-sqs.sh manual)
- Remover SqsConfig.cs, SqsTracePropagation.cs, polling loops manuais
- Remover GetQueueUrlAsync, SendMessageAsync, ReceiveMessageAsync manuais
- Trace propagation automatico via MassTransit OpenTelemetry (AddSource("MassTransit"))

**mt-idempotency** - PLANNED

- Remover IdempotencyStore.cs e tabela idempotency_keys
- Substituir por dupla camada MassTransit: saga correlation (layer 1) + EF Outbox DuplicateDetectionWindow (layer 2)
- Remover verificacoes TryGetAsync/SaveAsync dos 6 handlers nos 3 servicos
- Remover IdempotencyKey dos contratos de comando

**mt-concurrency** - PLANNED

- Adicionar RowVersion (byte[]) no OrderSagaState com IsRowVersion() no DbContext
- Configurar ConcurrencyMode.Optimistic no registro do saga repository no OrderService
- Retry automatico pelo MassTransit em DbUpdateConcurrencyException
- Resolver TODO de divida tecnica existente no Worker.cs sobre validacao de concorrencia

**mt-outbox-dlq** - PLANNED

- Configurar AddEntityFrameworkOutbox<OrderDbContext> com UsePostgres(), DuplicateDetectionWindow, QueryDelay (DbContext unificado)
- Adicionar entidades Outbox no EF: AddOutboxMessageEntity(), AddOutboxStateEntity(), AddInboxStateEntity()
- Criar EF migration para tabelas do Outbox
- Resolver dual-write (HIGH-02 do code-review): mensagens salvas na mesma transacao que o estado
- Configurar retry/circuit breaker policies no MassTransit
- Remover endpoints GET /dlq e POST /dlq/redrive (~90 linhas)

**mt-db-migration** - PLANNED

- Unificar OrderDbContext + SagaDbContext em um unico DbContext no OrderService (Orders + SagaState + Outbox)
- Criar EF migration para novo schema: OrderSagaState com propriedades tipadas + RowVersion + tabelas Outbox
- Remover EnsureCreated() manual e CreateTablesAsync(), usar migrations do EF Core
- Atualizar init-db.sql para schema compativel com novo modelo

**mt-program-config** - PLANNED

- Configurar MassTransit individualmente no Program.cs de cada microsservico (4 configs independentes):
  - OrderService: AddMassTransit + AddSagaStateMachine + AddEntityFrameworkOutbox + Minimal API
  - PaymentService: AddMassTransit + AddConsumers (ProcessPayment, RefundPayment)
  - InventoryService: AddMassTransit + AddConsumers (ReserveInventory, ReleaseInventory)
  - ShippingService: AddMassTransit + AddConsumers (ScheduleShipping, CancelShipping)
- Adicionar pacotes NuGet: MassTransit, MassTransit.AmazonSQS, MassTransit.EntityFrameworkCore
- Remover ServiceCollectionExtensions manuais de SQS onde substituidos
- Remover registro manual de filas, polling workers e HttpClient para SagaOrchestrator

**mt-cleanup** - PLANNED

- Remover projeto SagaOrchestrator inteiro (csproj, Dockerfile, Worker.cs, Models/, StateMachine/, Data/)
- Remover Worker.cs dos 3 servicos (Payment, Inventory, Shipping) e do OrderService
- Remover SqsConfig.cs, SqsTracePropagation.cs, IdempotencyStore.cs, IdempotencyRecord.cs
- Remover reply queues e DLQs manuais do init-sqs.sh (9+ filas eliminadas)
- Remover SagaTerminatedNotification e fila order-status-updates
- Remover models/helpers de reply routing manual
- Atualizar saga-orchestration.sln (remover SagaOrchestrator, de 6 para 5 projetos)
- Limpar usings, references e dead code em todo o solution

**mt-infra-docker** - PLANNED

- Atualizar docker-compose.yml: 4 containers .NET (remover saga-orchestrator), portas ajustadas
- Remover Dockerfile do SagaOrchestrator
- Atualizar docker-compose.test.yml para 4 servicos
- Revisar init-sqs.sh (ou remover se MassTransit criar filas automaticamente)
- Atualizar init-db.sql com schema das tabelas Outbox + SagaState unificado
- Verificar compatibilidade LocalStack com MassTransit AmazonSQS transport
- Atualizar health check targets (4 servicos em vez de 5)

**mt-integration-tests** - PLANNED

- Adaptar DockerComposeFixture para 4 containers (remover saga-orchestrator health check)
- Atualizar SagaClient para apontar ao OrderService (endpoints /sagas agora em /orders ou unificados)
- Atualizar os 7 cenarios existentes (happy path, 3 compensacoes, isolamento, concorrencia)
- Novo cenario: validar que Order.Status atualiza para Completed/Failed (bug M9 resolvido)
- Validar que Outbox entrega mensagens corretamente em cenarios de falha
- Validar retry automatico em conflitos de concorrencia (RowVersion)
- Validar deduplicacao via DuplicateDetectionWindow
- Garantir 0 regressoes nos comportamentos existentes

**mt-demo-scripts** - PLANNED

- Atualizar scripts/happy-path-demo.sh para nova arquitetura (4 servicos, endpoints unificados)
- Atualizar scripts/concurrent-saga-demo.sh para novos endpoints/comportamentos
- Atualizar scripts/lib/common.sh: ajustar URLs e health checks (4 servicos)
- Remover referencias ao saga-orchestrator como servico separado
- Validar que todos os cenarios de demo continuam reproduziveis

**mt-docs-didaticos** - PLANNED

- Atualizar docs/01-fundamentos-sagas.md: justificativa da fusao OrderService + Orchestrator
- Reescrever docs/02-maquina-de-estados.md para MassTransit state machine declarativa
- Reescrever docs/03-padroes-compensacao.md para compensacao tipada (sem CompensationDataJson)
- Reescrever docs/04-idempotencia-retry.md para dupla camada MassTransit (saga correlation + Outbox)
- Reescrever docs/05-sqs-dlq-visibility.md para topologia simplificada + Outbox + retry policies
- Atualizar docs/06-opentelemetry-traces.md para instrumentacao automatica MassTransit
- Atualizar docs/07-concorrencia-sagas.md para RowVersion + ConcurrencyMode.Optimistic no saga state
- Atualizar docs/08-guia-pratico.md com novos comandos curl, fluxos MassTransit e 4 servicos
- Novo doc: docs/09-masstransit-overview.md — visao geral do framework, conceitos (state machine, consumers, outbox, transports)
- Novo doc: docs/10-comparativo-manual-vs-masstransit.md — antes/depois com metricas de linhas, filas, containers e complexidade

**mt-readme** - PLANNED

- Atualizar README.md com nova arquitetura MassTransit (4 microsservicos)
- Atualizar diagrama de arquitetura (OrderService como orquestrador, topologia de filas simplificada)
- Atualizar instrucoes de setup e pre-requisitos (novos pacotes NuGet)
- Atualizar exemplos de curl (endpoints unificados no OrderService)
- Atualizar tabela de portas (4 servicos) e estrutura de diretorios (sem SagaOrchestrator)
- Atualizar sumario dos docs didaticos (incluir novos docs 09 e 10)

**mt-codebase-docs** - PLANNED

- Criar/atualizar .specs/codebase/STACK.md com MassTransit + dependencias
- Criar/atualizar .specs/codebase/ARCHITECTURE.md com nova arquitetura (4 servicos, OrderService como orquestrador)
- Criar/atualizar .specs/codebase/CONVENTIONS.md com padroes MassTransit (naming, consumers, events)
- Criar/atualizar .specs/codebase/STRUCTURE.md com nova organizacao de pastas (sem SagaOrchestrator)
- Criar/atualizar .specs/codebase/TESTING.md com estrategia de testes MassTransit
- Criar/atualizar .specs/codebase/INTEGRATIONS.md com MassTransit + SQS + EF Core Outbox
- Criar/atualizar .specs/codebase/CONCERNS.md com riscos e debt pos-migracao

---

## Future Considerations

- Metricas OTel (counters, histograms, gauges) + dashboard Grafana
- Alertas no Grafana (latencia P95, sagas em estado Failed)
- Instrumentacao EF Core/Npgsql para traces de database
- Saga coreografada como PoC complementar para comparacao
- Testes de carga para demonstrar comportamento sob concorrencia
- Timeout e retry policies configuráveis no orquestrador
- Versionamento de comandos/eventos (schema evolution)
