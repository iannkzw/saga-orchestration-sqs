# Saga Orchestration — .NET + SQS + PostgreSQL

**Saga Orquestrada** em .NET 10, com Amazon SQS (via LocalStack) como transporte de mensagens e PostgreSQL como store de estado. O projeto demonstra, do zero ao código real, os principais padrões de sistemas distribuídos resilientes:

- **Happy path** — fluxo Order → Payment → Inventory → Shipping → `Completed`
- **Compensação em cascata** — falha em qualquer passo dispara rollback reverso automático
- **Idempotência** — handlers não reprocessam o mesmo comando duas vezes
- **DLQ visibility** — inspeção e redrive de mensagens mortas
- **Traces distribuídos** — OpenTelemetry propagado via SQS com W3C TraceContext
- **Observabilidade LGTM** — traces no Grafana/Tempo, logs no Grafana/Loki, correlacionados por TraceId
- **Concorrência com pessimistic locking** — SELECT FOR UPDATE vs race condition real

---

## Pré-requisitos

| Ferramenta | Versão mínima | Verificação |
|---|---|---|
| Docker | 24+ | `docker --version` |
| Docker Compose | v2 (plugin) | `docker compose version` |
| bash | 4+ | `bash --version` |
| curl | qualquer | `curl --version` |
| jq | 1.6+ | `jq --version` |

> **Windows**: use Git Bash, WSL2 ou qualquer terminal com bash nativo para os scripts de demo.

---

## Setup Rápido

```bash
# 1. Clone o repositório
git clone https://github.com/iannkzw/saga-orchestration-dotnet-sqs.git
cd saga-orchestration-dotnet-sqs

# 2. Na primeira execução, compile as imagens antes de subir
#    (evita timeout de health check durante restauração de pacotes NuGet)
docker compose build

# 3. Suba todos os serviços
docker compose up -d

# 3. Aguarde os containers ficarem healthy (~30s)
docker compose ps
```

Saída esperada de `docker compose ps` quando tudo está pronto:

```
NAME                      STATUS
saga-lgtm                 Up (healthy)
saga-otelcol              Up
saga-localstack           Up (healthy)
saga-postgres             Up (healthy)
saga-order-service        Up (healthy)
saga-orchestrator         Up (healthy)
saga-payment-service      Up (healthy)
saga-inventory-service    Up (healthy)
saga-shipping-service     Up (healthy)
```

Confirme individualmente com:

```bash
curl -s http://localhost:5001/health | jq .
curl -s http://localhost:5002/health | jq .
```

---

## Demos

### Happy Path — saga chega a `Completed`

```bash
# Criar um pedido
curl -s -X POST http://localhost:5001/orders \
  -H "Content-Type: application/json" \
  -d '{
    "totalAmount": 99.90,
    "items": [{"productId": "PROD-001", "quantity": 1, "unitPrice": 99.90}]
  }' | jq .
```

Resposta esperada:

```json
{
  "orderId": "3fa85f64-...",
  "sagaId": "7c9e6679-..."
}
```

Verifique o estado final da saga (aguarde ~2s para o orquestrador processar):

```bash
SAGA_ID="<sagaId da resposta acima>"

curl -s http://localhost:5002/sagas/$SAGA_ID | jq '{state: .currentState, transitions: [.transitions[].toState]}'
```

Resposta esperada:

```json
{
  "state": "Completed",
  "transitions": [
    "PaymentProcessing",
    "InventoryReserving",
    "ShippingScheduling",
    "Completed"
  ]
}
```

---

### Falha e Compensação — saga chega a `Failed`

Use o header `X-Simulate-Failure` para injetar falhas em qualquer passo:

#### Falha no Pagamento

```bash
curl -s -X POST http://localhost:5001/orders \
  -H "Content-Type: application/json" \
  -H "X-Simulate-Failure: payment" \
  -d '{"totalAmount": 50.00, "items": [{"productId": "PROD-001", "quantity": 1, "unitPrice": 50.00}]}' | jq .
```

Cascata: `PaymentProcessing → Failed` (nenhuma compensação necessária — nada foi confirmado)

#### Falha no Inventário

```bash
curl -s -X POST http://localhost:5001/orders \
  -H "Content-Type: application/json" \
  -H "X-Simulate-Failure: inventory" \
  -d '{"totalAmount": 50.00, "items": [{"productId": "PROD-001", "quantity": 1, "unitPrice": 50.00}]}' | jq .
```

Cascata: `InventoryReserving → PaymentRefunding → Failed` (estorno do pagamento)

#### Falha no Shipping

```bash
curl -s -X POST http://localhost:5001/orders \
  -H "Content-Type: application/json" \
  -H "X-Simulate-Failure: shipping" \
  -d '{"totalAmount": 50.00, "items": [{"productId": "PROD-001", "quantity": 1, "unitPrice": 50.00}]}' | jq .
```

Cascata: `ShippingCancelling → InventoryReleasing → PaymentRefunding → Failed` (rollback completo)

---

### Script Automatizado — 4 Cenários Sequenciais

```bash
bash scripts/happy-path-demo.sh
```

O script executa e valida automaticamente:
1. Happy path completo (`Completed` com 4 transições)
2. Falha no pagamento (`Failed` sem compensação)
3. Falha no inventário (`Failed` com estorno de pagamento)
4. Falha no shipping (`Failed` com cascata completa)

Saída de sucesso esperada:

```
✓ Cenário 1: Happy Path — OK
✓ Cenário 2: Falha no Pagamento — OK
✓ Cenário 3: Falha no Inventário — OK
✓ Cenário 4: Falha no Shipping — OK

4/4 cenários passaram.
```

---

### Concorrência — SELECT FOR UPDATE vs Race Condition

```bash
# COM lock (padrão) — resultado correto: 2 Completed + 3 Failed
bash scripts/concurrent-saga-demo.sh

# SEM lock — demonstra overbooking (race condition TOCTOU)
bash scripts/concurrent-saga-demo.sh --no-lock

# Personalizar
bash scripts/concurrent-saga-demo.sh --pedidos 5 --estoque 2
```

> **Nota sobre `--no-lock`**: requer reconfiguração do serviço com `INVENTORY_LOCKING_ENABLED=false` no `docker-compose.yml` e rebuild do container.

Com lock ativo (`INVENTORY_LOCKING_ENABLED=true`, padrão), o resultado esperado com estoque=2 e 5 pedidos:

```
Resultado: 2 Completed + 3 Failed
Estoque final: 0 (nenhum overbooking)
```

Sem lock, é possível observar mais de 2 sagas `Completed` com estoque insuficiente (overbooking).

---

### DLQ Visibility — Inspecionar e Reprocessar Mensagens

Liste todas as mensagens nas Dead Letter Queues:

```bash
curl -s http://localhost:5002/dlq | jq .
```

Resposta de exemplo:

```json
[
  {
    "queueName": "payment-commands-dlq",
    "messageId": "abc123",
    "body": "{\"sagaId\":\"...\",\"commandType\":\"ProcessPayment\"}",
    "approximateReceiveCount": "3",
    "sentTimestamp": "1711900000000"
  }
]
```

Reenviar uma mensagem para a fila original:

```bash
curl -s -X POST http://localhost:5002/dlq/redrive \
  -H "Content-Type: application/json" \
  -d '{
    "queueName": "payment-commands-dlq",
    "receiptHandle": "<receiptHandle da mensagem acima>"
  }' | jq .
```

---

## Observabilidade — LGTM Stack

O projeto integra a stack **LGTM** (Grafana + Tempo + Loki) via um OTel Collector centralizado. Os 5 serviços .NET exportam traces e logs via OTLP gRPC para o Collector, que aplica tail sampling (descarta `GET /health` com status OK, mantém erros) e encaminha ao backend `grafana/otel-lgtm` (all-in-one).

Acesse **http://localhost:3000** após `docker compose up -d`. Use **Explore → Tempo** para buscar traces por `service.name` ou `saga.id`, e **Explore → Loki** com `{service_name="<serviço>"}` para logs. Os datasources têm link bidirecional: um `TraceID` no log navega direto ao trace, e um span no Tempo mostra os logs correlacionados.

Os logs do `ILogger<T>` são exportados via OTLP com correlação automática de `TraceId`/`SpanId` (método `AddSagaLogging()` em `Shared/Extensions/ServiceCollectionExtensions.cs`). Cada serviço tem `OTEL_EXPORTER_OTLP_ENDPOINT=http://otelcol:4317` e `OTEL_SERVICE_NAME` em kebab-case configurados no `docker-compose.yml`. Sem o endpoint definido, o console exporter é usado como fallback.

---

## Testes de Integração

O projeto inclui uma suíte de 8 testes de integração end-to-end que sobe o ambiente completo via Docker Compose e valida todos os cenários principais.

### Pré-requisitos

Além dos pré-requisitos gerais, é necessário o SDK .NET 10:

```bash
dotnet --version   # deve retornar 10.x
```

### Executar

```bash
dotnet test tests/IntegrationTests/
```

O runner cuida de tudo automaticamente:
1. Builda as imagens Docker (usa cache quando possível)
2. Sobe LocalStack, PostgreSQL e os 5 serviços
3. Aguarda as filas SQS serem criadas e os health checks passarem
4. Executa os 8 testes em sequência
5. Derruba e limpa o ambiente (`down -v`)

Tempo esperado na primeira execução (build das imagens): **~3–5 minutos**.  
Execuções subsequentes (imagens em cache): **~1–2 minutos**.

### Testes incluídos

| ID | Classe | Cenário |
|---|---|---|
| T1 | `HappyPathTests` | Pedido válido — saga atinge `Completed` com todas as transições |
| T2 | `CompensationTests` | Falha no pagamento — saga termina `Failed` sem compensação |
| T3 | `CompensationTests` | Falha no inventário — `Failed` com `PaymentRefunding` |
| T4 | `CompensationTests` | Falha no shipping — `Failed` com cascata completa |
| T5a | `IdempotencyTests` | Dois pedidos simultâneos — ambos completam sem corrupção de estado |
| T5b | `IdempotencyTests` | Pedido falho não corrompe pedido bem-sucedido concorrente |
| T6 | `ConcurrencyTests` | 5 pedidos simultâneos com estoque=2 e lock pessimista — exatamente 2 completam |
| T7 | `ConcurrencyTests` | Comportamento sem lock (documentacional — sempre passa) |

### Saída esperada

```
Aprovado IntegrationTests.Tests.CompensationTests.PaymentFailure_SagaFails_NoCompensation
Aprovado IntegrationTests.Tests.CompensationTests.ShippingFailure_SagaFails_InventoryAndPaymentCompensated
Aprovado IntegrationTests.Tests.CompensationTests.InventoryFailure_SagaFails_PaymentRefunded
Aprovado IntegrationTests.Tests.ConcurrencyTests.WithoutLock_DocumentsBehavior_AlwaysPasses
Aprovado IntegrationTests.Tests.ConcurrencyTests.WithPessimisticLock_ExactlyTwoComplete_NoOverbooking
Aprovado IntegrationTests.Tests.HappyPathTests.PostOrder_ValidProduct_SagaCompletes
Aprovado IntegrationTests.Tests.IdempotencyTests.TwoConcurrentOrders_BothComplete_NoStateCorruption
Aprovado IntegrationTests.Tests.IdempotencyTests.FailingOrderDoesNotCorruptConcurrentSuccessfulOrder

Total de testes: 8 | Aprovados: 8
```

### Detalhes da infraestrutura de testes

Os testes usam um compose override (`tests/IntegrationTests/docker-compose.test.yml`) que:
- Fixa as portas 5001–5005 para os serviços
- Força `INVENTORY_LOCKING_MODE=pessimistic` (necessário para T6)
- Adiciona `restart: unless-stopped` para tolerar o race entre o startup dos serviços e a criação assíncrona das filas SQS pelo `init-sqs.sh`

---

## Estrutura do Projeto

```
saga-orchestration-dotnet-sqs/
│
├── src/                          # Código-fonte dos serviços .NET
│   ├── OrderService/             # API HTTP — recebe pedidos e inicia sagas
│   ├── SagaOrchestrator/         # Orquestrador — maquina de estados + DLQ endpoints
│   ├── PaymentService/           # Worker SQS — processa/compensa pagamentos
│   ├── InventoryService/         # Worker SQS — reserva/libera estoque (SELECT FOR UPDATE)
│   ├── ShippingService/          # Worker SQS — agenda/cancela entregas
│   └── Shared/                   # Contracts, SQS helpers, Idempotency, OpenTelemetry
│
├── docs/                         # Documentação didática (8 artigos)
│
├── scripts/                      # Scripts bash para demos
│   ├── lib/common.sh             # Funções compartilhadas (check_health, poll_saga, etc.)
│   ├── happy-path-demo.sh        # 4 cenários sequenciais com verificação automática
│   └── concurrent-saga-demo.sh   # Demo de concorrência com/sem lock
│
├── tests/
│   └── IntegrationTests/         # Suíte de 8 testes E2E (xUnit + Docker Compose)
│       ├── Tests/                # HappyPath, Compensation, Idempotency, Concurrency
│       ├── Infrastructure/       # DockerComposeFixture, SagaClient, InventoryClient
│       └── docker-compose.test.yml  # Override de portas e locking mode para testes
│
├── infra/                        # Configuração de infraestrutura local
│   ├── localstack/               # Init script de criação das filas SQS
│   ├── postgres/                 # Init SQL com schemas do PostgreSQL
│   ├── otel/                     # Configuração do OTel Collector
│   │   ├── otelcol.yaml          # Receivers, processors, exporters e pipelines
│   │   └── processors/sampling/  # Políticas de tail sampling (drop health checks, keep errors)
│   └── grafana/                  # Grafana provisioning automático
│       ├── provisioning/
│       │   ├── datasources/      # Datasources Tempo e Loki (com link bidirecional)
│       │   └── dashboards/       # Provider de dashboards
│       └── dashboards/           # Dashboard "Saga Orchestration — Overview" (JSON)
│
└── docker-compose.yml            # Orquestração completa do ambiente local
```

---

## Portas dos Serviços

| Serviço | Porta | Health Check |
|---|---|---|
| LocalStack (SQS) | 4566 | `curl http://localhost:4566/_localstack/health` |
| PostgreSQL | 5432 | — (acesso interno) |
| Grafana (LGTM) | 3000 | `curl http://localhost:3000/api/health` |
| OTel Collector (gRPC) | 4317 | — (ingress OTLP) |
| OTel Collector (HTTP) | 4318 | — (ingress OTLP) |
| OrderService | 5001 | `curl http://localhost:5001/health` |
| SagaOrchestrator | 5002 | `curl http://localhost:5002/health` |
| PaymentService | 5003 | `curl http://localhost:5003/health` |
| InventoryService | 5004 | `curl http://localhost:5004/health` |
| ShippingService | 5005 | `curl http://localhost:5005/health` |

Endpoints adicionais do InventoryService:

```bash
GET  http://localhost:5004/inventory/stock/{productId}   # Consultar estoque
POST http://localhost:5004/inventory/reset               # Resetar estoque para demos
```

---

## Documentação Didática

Oito artigos em [`docs/`](docs/) que aprofundam cada padrão implementado:

| Documento | O que cobre |
|---|---|
| [01-fundamentos-sagas.md](docs/01-fundamentos-sagas.md) | Saga vs 2PC, orquestrada vs coreografada, justificativa da escolha |
| [02-maquina-de-estados.md](docs/02-maquina-de-estados.md) | Diagrama completo de estados, transições forward e de compensação |
| [03-padroes-compensacao.md](docs/03-padroes-compensacao.md) | Cascata reversa, CompensationDataJson, implementação do rollback |
| [04-idempotencia-retry.md](docs/04-idempotencia-retry.md) | IdempotencyStore com Npgsql, chaves por saga, visibility timeout |
| [05-sqs-dlq-visibility.md](docs/05-sqs-dlq-visibility.md) | Topologia de filas, RedrivePolicy, endpoints GET/POST /dlq |
| [06-opentelemetry-traces.md](docs/06-opentelemetry-traces.md) | W3C TraceContext sobre SQS, SagaActivitySource, exporters OTLP, stack LGTM |
| [07-concorrencia-sagas.md](docs/07-concorrencia-sagas.md) | Race conditions, pessimistic vs optimistic locking, SELECT FOR UPDATE |
| [08-guia-pratico.md](docs/08-guia-pratico.md) | Passo a passo completo de todos os cenários, troubleshooting |
