# Spec: integration-tests

## Objetivo

Criar suite de testes de integração (.NET 10, xUnit) que sobe o ambiente completo via Docker Compose,
dispara cenários reais contra os serviços e verifica os estados finais das sagas.
A suite deve ser executável com um único comando (`dotnet test`) sem dependência de setup manual.

---

## Requisitos

### R1 — Projeto xUnit independente

O projeto `tests/IntegrationTests/IntegrationTests.csproj` deve:
- Ser um projeto xUnit (.NET 10) referenciando o Shared do projeto principal
- Ser executável via `dotnet test tests/IntegrationTests/`
- Não depender de ambiente pré-existente (subir e derrubar o compose automaticamente)

### R2 — Fixture de Docker Compose

Uma fixture `DockerComposeFixture` deve:
- Implementar `IAsyncLifetime`
- Executar `docker compose -f docker-compose.yml -f docker-compose.test.yml up -d` no `InitializeAsync`
- Aguardar health checks de todos os serviços antes de liberar os testes
- Executar `docker compose down -v` no `DisposeAsync`
- Ser compartilhada como `ICollectionFixture` entre todos os testes (subir uma vez por suite)

### R3 — Clientes HTTP helper

- `SagaClient`: encapsula `POST /orders` (OrderService:5001) e `GET /sagas/{id}` (SagaOrchestrator:5002)
- `InventoryClient`: encapsula `GET /inventory/stock/{productId}` e `POST /inventory/reset` (InventoryService:5004)
- Ambos expõem método `PollUntilTerminal(sagaId, timeout, interval)` para aguardar estado terminal

### R4 — Polling com timeout

Sagas são assíncronas. Todos os testes que verificam estado final devem:
- Usar polling com timeout máximo de **30 segundos**
- Intervalo entre tentativas de **500ms**
- Lançar `TimeoutException` com mensagem descritiva se o estado terminal não for atingido

### R5 — Cenário: Happy path

**ID:** T1  
**Critério de aceite:**
- `POST /orders` com produto válido retorna 200 com `orderId` e `sagaId`
- Após polling, `GET /sagas/{sagaId}` retorna `CurrentState = "Completed"`
- Histórico de transições contém: `Pending → PaymentProcessing → InventoryReserving → ShippingScheduling → Completed`

### R6 — Cenário: Falha no pagamento

**ID:** T2  
**Critério de aceite:**
- `POST /orders` com header `X-Simulate-Failure: payment`
- Após polling, saga atinge `CurrentState = "Failed"`
- Histórico **não** contém `InventoryReleasing` nem `PaymentRefunding` (nada foi completado para compensar)

### R7 — Cenário: Falha no inventário

**ID:** T3  
**Critério de aceite:**
- `POST /orders` com header `X-Simulate-Failure: inventory`
- Após polling, saga atinge `CurrentState = "Failed"`
- Histórico contém `PaymentRefunding` (pagamento foi feito, precisa ser reembolsado)
- Histórico **não** contém `InventoryReleasing`

### R8 — Cenário: Falha no shipping

**ID:** T4  
**Critério de aceite:**
- `POST /orders` com header `X-Simulate-Failure: shipping`
- Após polling, saga atinge `CurrentState = "Failed"`
- Histórico contém `InventoryReleasing` e `PaymentRefunding`

### R9 — Cenário: Idempotência

**ID:** T5  
**Critério de aceite:**
- Mesma `IdempotencyKey` enviada duas vezes (via header `X-Idempotency-Key`)
- Ambas as chamadas retornam o mesmo `sagaId`
- Apenas uma saga é criada (verificar via `GET /sagas/{sagaId}`)
- A saga não duplica transições

### R10 — Cenário: Concorrência com lock

**ID:** T6  
**Critério de aceite:**
- Ambiente com `INVENTORY_LOCKING_MODE=pessimistic`
- Estoque resetado para 2 unidades de `PROD-TEST-CONCURRENT`
- 5 pedidos disparados concorrentemente para o mesmo produto
- Após polling de todas as 5 sagas: exatamente **2 Completed** e **3 Failed**
- Estoque final: **0** (não houve overbooking)

### R11 — Cenário: Concorrência sem lock (documentacional)

**ID:** T7  
**Critério de aceite:**
- Ambiente com `INVENTORY_LOCKING_MODE=none`
- Estoque resetado para 2 unidades
- 5 pedidos disparados concorrentemente
- Teste documenta o resultado observado (pode haver overbooking)
- Teste **não** falha se mais de 2 pedidos forem `Completed` — o objetivo é evidenciar o comportamento

### R12 — Isolamento entre testes

- Testes de concorrência usam produto dedicado (`PROD-TEST-CONCURRENT`) diferente do produto padrão (`PROD-001`)
- `POST /inventory/reset` é chamado no início de cada teste que depende de estoque controlado
- Testes de happy path / compensação usam `PROD-001` com estoque suficiente (reset para 100 no início)

### R13 — Override Docker Compose para testes

- `docker-compose.test.yml` define portas fixas para todos os serviços (5001–5005)
- Não altera a configuração de produção (`docker-compose.yml`)

---

## Definição de "Pronto"

A suite está pronta quando:
1. `dotnet test tests/IntegrationTests/` executa sem erros de compilação
2. T1–T5 passam de forma determinística em ambiente limpo
3. T6 passa de forma determinística com lock habilitado
4. T7 executa e registra o comportamento observado sem falhar o CI
5. A fixture sobe e derruba o compose automaticamente (sem estado residual entre runs)
