# Project Skeleton - Specification

## Problem Statement

Os servicos .NET existem como placeholders isolados sem estrutura de solution, sem projeto compartilhado (Shared) e sem conectividade real com SQS/PostgreSQL. Para avancar para M2 (saga happy path), e necessario ter uma base .NET estruturada com configuracoes centralizadas, contracts compartilhados e conectividade validada.

## Goals

- [ ] Solution .NET estruturada com 5 projetos de servico + 1 projeto Shared
- [ ] Configuracao centralizada via Directory.Build.props e global.json (.NET 10)
- [ ] Projeto Shared com contracts (commands/replies), SQS helpers e modelo de IdempotencyKey
- [ ] OrderService como Minimal API, demais servicos como Worker Services com endpoint /health
- [ ] Cada servico conecta ao SQS (list queues) e PostgreSQL (test connection) como smoke test na inicializacao

## Out of Scope

| Feature | Reason |
| --- | --- |
| Logica de negocio nos servicos | Sera implementada em M2 (saga happy path) |
| EF Core migrations / DbContext completo | Sera implementado em M2 com modelos de dominio |
| Consumer/producer SQS real | Sera implementado em M2 (command-reply-flow) |
| Testes unitarios | Sera adicionado conforme logica de negocio surgir |
| Dockerfile multi-stage otimizado | Dockerfiles serao adaptados para solution build mas sem otimizacao |

---

## User Stories

### P1: Solution .NET Estruturada ⭐ MVP

**User Story**: Como desenvolvedor, quero abrir a solution no IDE e ver todos os projetos organizados, para navegar e compilar o codigo facilmente.

**Why P1**: Sem solution estruturada, o desenvolvimento e ineficiente e propenso a inconsistencias.

**Acceptance Criteria**:

1. WHEN `dotnet build` e executado na raiz THEN todos os 6 projetos (OrderService, SagaOrchestrator, PaymentService, InventoryService, ShippingService, Shared) SHALL compilar sem erros
2. WHEN o arquivo .sln e aberto no IDE THEN todos os projetos SHALL estar listados e navegaveis
3. WHEN `global.json` e lido THEN SHALL especificar .NET 10 como SDK version
4. WHEN `Directory.Build.props` e lido THEN SHALL definir TargetFramework, ImplicitUsings e Nullable centralizados

**Independent Test**: `dotnet build SagaOrchestration.sln` na raiz deve compilar sem erros.

---

### P1: Projeto Shared com Contracts ⭐ MVP

**User Story**: Como desenvolvedor, quero ter contracts (commands/replies) e utilitarios compartilhados em um projeto unico, para evitar duplicacao e garantir tipagem forte entre servicos.

**Why P1**: Sem contracts tipados, a comunicacao entre servicos sera fragil e propensa a erros de serializacao.

**Acceptance Criteria**:

1. WHEN o projeto Shared e referenciado por um servico THEN os contracts (commands e replies) SHALL estar disponiveis para uso
2. WHEN um command e criado THEN SHALL conter pelo menos SagaId (Guid), IdempotencyKey (string) e Timestamp (DateTime)
3. WHEN um reply e criado THEN SHALL conter pelo menos SagaId (Guid), Success (bool) e ErrorMessage (string?)
4. WHEN o modelo IdempotencyRecord e usado THEN SHALL conter IdempotencyKey, ProcessedAt e ResponsePayload

**Independent Test**: Projeto compila e tipos sao usaveis nos servicos.

---

### P1: Smoke Test de Conectividade ⭐ MVP

**User Story**: Como desenvolvedor, quero que cada servico valide conectividade com SQS e PostgreSQL ao iniciar, para ter confianca de que a infraestrutura esta acessivel.

**Why P1**: Sem validacao de conectividade, falhas de configuracao so serao descobertas em tempo de execucao nos milestones seguintes.

**Acceptance Criteria**:

1. WHEN um servico .NET inicia dentro do Docker Compose THEN SHALL logar no console se a conexao com PostgreSQL foi bem-sucedida
2. WHEN um servico .NET inicia dentro do Docker Compose THEN SHALL logar no console se a conexao com SQS (list queues) foi bem-sucedida
3. WHEN SQS ou PostgreSQL esta inacessivel THEN o servico SHALL logar um warning mas continuar rodando (graceful degradation)
4. WHEN o endpoint /health e acessado THEN SHALL retornar status das conexoes (SQS e PostgreSQL)

**Independent Test**: `docker compose up -d && docker compose logs order-service` deve mostrar logs de conectividade.

---

### P2: Worker Services para Servicos de Backend

**User Story**: Como desenvolvedor, quero que Payment, Inventory, Shipping e SagaOrchestrator usem o template Worker Service, para que possam processar mensagens SQS em background.

**Why P2**: Worker Services sao o template correto para consumers SQS, mas a logica de consume sera implementada em M2.

**Acceptance Criteria**:

1. WHEN PaymentService, InventoryService e ShippingService sao configurados THEN SHALL usar Worker Service template com IHostedService
2. WHEN o SagaOrchestrator e configurado THEN SHALL usar Worker Service template com IHostedService
3. WHEN cada worker service inicia THEN SHALL expor endpoint /health via Minimal API (para health checks do Docker Compose)
4. WHEN o OrderService e configurado THEN SHALL permanecer como Minimal API (nao worker)

**Independent Test**: `docker compose up -d && docker compose ps` — todos os servicos devem estar healthy.

---

## Edge Cases

- WHEN o .NET SDK 10 nao esta instalado localmente THEN `dotnet build` SHALL falhar com mensagem clara referenciando global.json
- WHEN a connection string do PostgreSQL esta incorreta THEN o smoke test SHALL logar warning e nao crashar o servico
- WHEN o LocalStack ainda nao esta ready THEN o smoke test de SQS SHALL logar warning (depends_on ja garante health check)
- WHEN um projeto Shared tem breaking change THEN todos os servicos dependentes SHALL falhar na compilacao (tipagem forte)

---

## Requirement Traceability

| Requirement ID | Story | Phase | Status |
| --- | --- | --- | --- |
| SKEL-01 | P1: Solution .NET - .sln, global.json, Directory.Build.props | Plan | Planned |
| SKEL-02 | P1: Projeto Shared - contracts (commands/replies) | Plan | Planned |
| SKEL-03 | P1: Projeto Shared - SQS helpers | Plan | Planned |
| SKEL-04 | P1: Projeto Shared - IdempotencyRecord model | Plan | Planned |
| SKEL-05 | P1: Smoke test conectividade SQS | Plan | Planned |
| SKEL-06 | P1: Smoke test conectividade PostgreSQL | Plan | Planned |
| SKEL-07 | P2: Worker Services (Payment, Inventory, Shipping, Orchestrator) | Plan | Planned |
| SKEL-08 | P2: OrderService como Minimal API | Plan | Planned |
| SKEL-09 | Edge: Graceful degradation nos smoke tests | Plan | Planned |

**Coverage:** 9 total, 0 mapped to tasks, 9 unmapped

---

## Success Criteria

- [ ] `dotnet build SagaOrchestration.sln` compila sem erros
- [ ] Projeto Shared contem contracts tipados (commands, replies, IdempotencyRecord)
- [ ] `docker compose up -d` sobe todos os servicos com health checks passando
- [ ] Logs de cada servico mostram resultado do smoke test (SQS + PostgreSQL)
- [ ] OrderService e Minimal API; demais sao Worker Services com /health endpoint
