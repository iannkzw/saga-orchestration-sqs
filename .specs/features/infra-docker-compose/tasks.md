# Infra Docker Compose - Tasks

**Spec**: `.specs/features/infra-docker-compose/spec.md`
**Status**: Done

---

## Execution Plan

### Phase 1: Foundation (Sequential)

```
T1 → T2 → T3
```

### Phase 2: Config (Parallel)

```
T3 complete, then:
  ├── T4 [P]
  └── T5 [P]
```

### Phase 3: Compose (Sequential)

```
T4, T5 complete, then:
  T6 → T7
```

---

## Task Breakdown

### T1: Criar .env com variaveis de ambiente

**What**: Arquivo `.env` na raiz com todas as variaveis usadas no docker-compose (portas, credenciais, versoes)
**Where**: `.env`
**Depends on**: None
**Requirement**: INFRA-01, INFRA-02

**Done when**:

- [ ] `.env` criado com variaveis para portas (LocalStack, PostgreSQL, servicos .NET)
- [ ] Credenciais PostgreSQL configuradas (user, password, db)
- [ ] Endpoint AWS e regiao configurados para LocalStack

**Verify**: `cat .env` — deve listar todas as variaveis sem valores vazios

---

### T2: Criar init script SQS para LocalStack

**What**: Shell script que cria todas as filas SQS (principais + DLQs) usando awslocal
**Where**: `infra/localstack/init-sqs.sh`
**Depends on**: T1
**Requirement**: INFRA-03, INFRA-04

**Done when**:

- [ ] Script cria 8 filas principais (order-commands, payment-commands, payment-replies, inventory-commands, inventory-replies, shipping-commands, shipping-replies, saga-commands)
- [ ] Script cria 8 DLQs correspondentes (sufixo -dlq)
- [ ] Cada fila principal tem RedrivePolicy apontando para sua DLQ (maxReceiveCount=3)
- [ ] Script e executavel (chmod +x)

**Verify**: Script pode ser inspecionado — `awslocal sqs list-queues` sera testado no T7

---

### T3: Criar init script PostgreSQL

**What**: SQL script de inicializacao que cria database e extensoes necessarias
**Where**: `infra/postgres/init.sql`
**Depends on**: T1
**Requirement**: INFRA-05

**Done when**:

- [ ] Script cria database `saga_db` (ou usa o default do POSTGRES_DB)
- [ ] Extensoes basicas habilitadas (uuid-ossp)
- [ ] Script e idempotente (IF NOT EXISTS)

**Verify**: Script pode ser inspecionado — conexao sera testada no T7

---

### T4: Criar Dockerfiles placeholder para servicos .NET [P]

**What**: Dockerfiles simples para cada servico .NET que apenas expoe uma porta e responde health check
**Where**: `src/OrderService/Dockerfile`, `src/SagaOrchestrator/Dockerfile`, `src/PaymentService/Dockerfile`, `src/InventoryService/Dockerfile`, `src/ShippingService/Dockerfile`
**Depends on**: T3
**Requirement**: INFRA-01

**Done when**:

- [ ] 5 Dockerfiles criados, um por servico
- [ ] Cada um usa imagem base .NET 10 SDK
- [ ] Cada um tem um Program.cs minimo com health endpoint GET /health retornando 200
- [ ] Cada um tem .csproj minimo

**Verify**: `docker build` em cada diretorio deve completar sem erros

---

### T5: Criar .dockerignore [P]

**What**: Arquivo .dockerignore na raiz para excluir artefatos desnecessarios do build context
**Where**: `.dockerignore`
**Depends on**: T3
**Requirement**: INFRA-01

**Done when**:

- [ ] Exclui bin/, obj/, .git, .specs, .env, *.md

**Verify**: `cat .dockerignore` lista os padroes

---

### T6: Criar docker-compose.yml

**What**: Arquivo Docker Compose unificado com todos os servicos, infra e configuracoes
**Where**: `docker-compose.yml`
**Depends on**: T4, T5
**Requirement**: INFRA-01, INFRA-02, INFRA-06, INFRA-07

**Done when**:

- [ ] Servico `localstack` configurado com health check (/_localstack/health)
- [ ] Servico `postgres` configurado com health check (pg_isready)
- [ ] 5 servicos .NET configurados com build context e portas mapeadas
- [ ] Servicos .NET dependem de localstack e postgres via `depends_on: condition: service_healthy`
- [ ] Init script SQS montado como volume no LocalStack (/etc/localstack/init/ready.d/)
- [ ] Init script SQL montado como volume no PostgreSQL (/docker-entrypoint-initdb.d/)
- [ ] Variaveis de ambiente referenciadas do .env
- [ ] Rede custom compartilhada entre todos os servicos

**Verify**: `docker compose config` deve parsear sem erros

---

### T7: Validacao end-to-end

**What**: Subir todo o ambiente e validar que todos os criterios de aceite passam
**Where**: N/A (validacao manual)
**Depends on**: T6
**Requirement**: Todos

**Done when**:

- [ ] `docker compose up -d` completa sem erros
- [ ] `docker compose ps` mostra localstack e postgres como healthy
- [ ] `docker compose exec localstack awslocal sqs list-queues` lista 16 filas
- [ ] `docker compose exec postgres psql -U saga -d saga_db -c '\l'` conecta com sucesso
- [ ] Servicos .NET respondem em suas portas (/health retorna 200)
- [ ] `docker compose down` remove tudo sem erros

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
    T6 → T7
```

---

## Task Granularity Check

| Task | Scope | Status |
| --- | --- | --- |
| T1: .env | 1 arquivo | Granular |
| T2: Init SQS | 1 script | Granular |
| T3: Init SQL | 1 script | Granular |
| T4: Dockerfiles | 5 arquivos (coesivos) | OK |
| T5: .dockerignore | 1 arquivo | Granular |
| T6: docker-compose.yml | 1 arquivo | Granular |
| T7: Validacao | Teste manual | Granular |
