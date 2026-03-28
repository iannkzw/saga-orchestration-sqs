# Project Skeleton - Tasks

**Spec**: `.specs/features/project-skeleton/spec.md`
**Status**: Done

---

## Execution Plan

### Phase 1: Foundation (Sequential)

```
T1 → T2 → T3
```

### Phase 2: Shared + Services (Parallel)

```
T3 complete, then:
  ├── T4 [P]  (Shared project)
  └── T5 [P]  (Worker Services refactor)
```

### Phase 3: Integration (Sequential)

```
T4, T5 complete, then:
  T6 → T7 → T8
```

---

## Task Breakdown

### T1: Criar global.json e Directory.Build.props

**What**: Arquivos de configuracao centralizada .NET na raiz do repositorio
**Where**: `global.json`, `src/Directory.Build.props`
**Depends on**: None
**Requirement**: SKEL-01

**Done when**:

- [ ] `global.json` criado na raiz com SDK version `10.0.100-preview.*` (rollForward: latestFeature)
- [ ] `src/Directory.Build.props` criado com TargetFramework=net10.0, ImplicitUsings=enable, Nullable=enable
- [ ] .csproj dos servicos existentes simplificados (removem TargetFramework e ImplicitUsings duplicados)

**Verify**: `dotnet --version` e `cat global.json` / `cat src/Directory.Build.props`

---

### T2: Criar Solution (.sln) e projeto Shared

**What**: Arquivo .sln na raiz com todos os projetos, e projeto class library Shared
**Where**: `SagaOrchestration.sln`, `src/Shared/Shared.csproj`
**Depends on**: T1
**Requirement**: SKEL-01, SKEL-02

**Done when**:

- [ ] `SagaOrchestration.sln` criado na raiz
- [ ] Todos os 5 projetos existentes adicionados ao .sln
- [ ] Projeto `src/Shared/Shared.csproj` criado como class library
- [ ] Shared adicionado ao .sln
- [ ] Todos os projetos de servico referenciam Shared

**Verify**: `dotnet build SagaOrchestration.sln` compila sem erros

---

### T3: Criar contracts no projeto Shared

**What**: Commands, replies e modelos compartilhados
**Where**: `src/Shared/Contracts/Commands/`, `src/Shared/Contracts/Replies/`, `src/Shared/Models/`
**Depends on**: T2
**Requirement**: SKEL-02, SKEL-03, SKEL-04

**Done when**:

- [ ] `BaseCommand.cs` com SagaId (Guid), IdempotencyKey (string), Timestamp (DateTime)
- [ ] Commands tipados: ProcessPayment, ReserveInventory, ScheduleShipping, CreateOrder
- [ ] `BaseReply.cs` com SagaId (Guid), Success (bool), ErrorMessage (string?)
- [ ] Replies tipados: PaymentReply, InventoryReply, ShippingReply
- [ ] `IdempotencyRecord.cs` com IdempotencyKey, ProcessedAt, ResponsePayload
- [ ] `SqsConfig.cs` com constantes de nomes de filas

**Verify**: `dotnet build src/Shared/Shared.csproj` compila sem erros

---

### T4: Adicionar pacotes NuGet necessarios [P]

**What**: AWSSDK.SQS e Npgsql nos projetos que precisam
**Where**: `src/Directory.Build.props` ou .csproj individuais
**Depends on**: T3
**Requirement**: SKEL-03, SKEL-05, SKEL-06

**Done when**:

- [ ] AWSSDK.SQS adicionado a todos os projetos de servico
- [ ] Npgsql adicionado a todos os projetos de servico
- [ ] `dotnet restore` executa sem erros

**Verify**: `dotnet restore SagaOrchestration.sln`

---

### T5: Refatorar servicos para Worker Service template [P]

**What**: PaymentService, InventoryService, ShippingService e SagaOrchestrator como Worker Services com /health
**Where**: `src/PaymentService/`, `src/InventoryService/`, `src/ShippingService/`, `src/SagaOrchestrator/`
**Depends on**: T3
**Requirement**: SKEL-07, SKEL-08

**Done when**:

- [ ] PaymentService, InventoryService, ShippingService usam Worker Service template
- [ ] SagaOrchestrator usa Worker Service template
- [ ] Cada worker service expoe /health via Minimal API (WebApplication junto com IHostedService)
- [ ] OrderService permanece como Minimal API pura (nao e worker)
- [ ] Todos compilam sem erros

**Verify**: `dotnet build SagaOrchestration.sln`

---

### T6: Implementar smoke test de conectividade

**What**: Startup check que valida conexao com SQS e PostgreSQL, loga resultado
**Where**: `src/Shared/HealthChecks/`, Program.cs de cada servico
**Depends on**: T4, T5
**Requirement**: SKEL-05, SKEL-06, SKEL-09

**Done when**:

- [ ] Classe SqsConnectivityCheck no Shared: faz SQS ListQueues, loga resultado
- [ ] Classe PostgresConnectivityCheck no Shared: faz Npgsql connection open, loga resultado
- [ ] Cada servico executa ambos os checks na inicializacao
- [ ] Falha de conexao loga warning mas nao impede startup
- [ ] Endpoint /health inclui status das conexoes

**Verify**: `dotnet build SagaOrchestration.sln`

---

### T7: Atualizar Dockerfiles para solution build

**What**: Dockerfiles adaptados para copiar Shared e compilar via solution
**Where**: `src/*/Dockerfile`
**Depends on**: T6
**Requirement**: SKEL-01

**Done when**:

- [ ] Dockerfiles copiam Directory.Build.props, global.json e projeto Shared
- [ ] Build usa `dotnet publish` do projeto especifico
- [ ] Todos os servicos compilam via `docker compose build`

**Verify**: `docker compose build` sem erros

---

### T8: Validacao end-to-end

**What**: Subir ambiente completo e validar todos os criterios de aceite
**Where**: N/A (validacao manual)
**Depends on**: T7
**Requirement**: Todos

**Done when**:

- [ ] `docker compose build` completa sem erros
- [ ] `docker compose up -d` sobe todos os servicos
- [ ] `docker compose ps` mostra todos como healthy
- [ ] Logs de cada servico mostram smoke test de SQS e PostgreSQL
- [ ] `curl localhost:5001/health` retorna status com conexoes
- [ ] `docker compose down` limpa tudo

**Verify**: Executar comandos acima sequencialmente

---

## Parallel Execution Map

```
Phase 1 (Sequential):
  T1 → T2 → T3

Phase 2 (Parallel):
  T3 complete, then:
    ├── T4 [P]  } Simultaneo
    └── T5 [P]

Phase 3 (Sequential):
  T4, T5 complete, then:
    T6 → T7 → T8
```

---

## Task Granularity Check

| Task | Scope | Status |
| --- | --- | --- |
| T1: global.json + Directory.Build.props | 2 arquivos + cleanup csproj | Granular |
| T2: .sln + Shared project | 2 arquivos | Granular |
| T3: Contracts no Shared | ~10 arquivos (coesivos) | OK |
| T4: Pacotes NuGet | Config de dependencias | Granular |
| T5: Worker Services | 4 servicos (coesivos) | OK |
| T6: Smoke tests | Shared + 5 Program.cs | OK |
| T7: Dockerfiles | 5 arquivos (coesivos) | OK |
| T8: Validacao | Teste manual | Granular |
