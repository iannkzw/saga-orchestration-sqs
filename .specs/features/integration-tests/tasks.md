# Tasks: integration-tests

## T1 — Setup do projeto xUnit

**Status:** pending

- Criar `tests/IntegrationTests/IntegrationTests.csproj` com .NET 10, xUnit 2.9, Microsoft.NET.Test.Sdk
- Criar `tests/IntegrationTests/` com subdiretórios: `Infrastructure/`, `Models/`, `Tests/`
- Adicionar o projeto à solution: `dotnet sln add tests/IntegrationTests/IntegrationTests.csproj`
- Verificar: `dotnet build tests/IntegrationTests/` compila sem erros

**Dependências:** nenhuma

---

## T2 — docker-compose.test.yml

**Status:** pending

- Criar `tests/IntegrationTests/docker-compose.test.yml` com override de portas fixas:
  - `order-service`: 5001:8080
  - `saga-orchestrator`: 5002:8080
  - `payment-service`: 5003:8080
  - `inventory-service`: 5004:8080
  - `shipping-service`: 5005:8080
  - `INVENTORY_LOCKING_MODE=pessimistic` no inventory-service
- Verificar: `docker compose -f docker-compose.yml -f tests/IntegrationTests/docker-compose.test.yml config` valida sem erros

**Dependências:** nenhuma

---

## T3 — DTOs de request/response

**Status:** pending

- Criar `tests/IntegrationTests/Models/CreateOrderRequest.cs`
- Criar `tests/IntegrationTests/Models/SagaResponse.cs` (com campo `CurrentState`, `Transitions`)
- Criar `tests/IntegrationTests/Models/StockResponse.cs`
- Verificar: compilação OK

**Dependências:** T1

---

## T4 — DockerComposeFixture

**Status:** pending

- Criar `tests/IntegrationTests/Infrastructure/DockerComposeFixture.cs`
  - `IAsyncLifetime`
  - `InitializeAsync`: roda `docker compose up -d`, polling HTTP `/health` em todos os 5 serviços, timeout 120s
  - `DisposeAsync`: roda `docker compose down -v`
  - Caminho relativo ao `docker-compose.yml` calculado a partir do assembly location
- Criar `tests/IntegrationTests/Infrastructure/TestCollectionDefinition.cs` com `[CollectionDefinition("Integration")]`
- Verificar: fixture inicializa sem exceção em ambiente com Docker disponível

**Dependências:** T1, T2

---

## T5 — SagaClient

**Status:** pending

- Criar `tests/IntegrationTests/Infrastructure/SagaClient.cs`
  - `PostOrderAsync(CreateOrderRequest, simulateFailure?, idempotencyKey?)` → retorna `(orderId, sagaId)`
  - `GetSagaAsync(Guid sagaId)` → retorna `SagaResponse`
  - `WaitForTerminalStateAsync(Guid sagaId, TimeSpan timeout, TimeSpan interval)` → retorna `SagaResponse` ou lança `TimeoutException`
- Verificar: compilação OK

**Dependências:** T1, T3

---

## T6 — InventoryClient

**Status:** pending

- Criar `tests/IntegrationTests/Infrastructure/InventoryClient.cs`
  - `GetStockAsync(string productId)` → retorna `StockResponse`
  - `ResetStockAsync(string productId, int quantity)` → void
- Verificar: compilação OK

**Dependências:** T1, T3

---

## T7 — HappyPathTests (T1 da spec)

**Status:** pending

- Criar `tests/IntegrationTests/Tests/HappyPathTests.cs`
- Teste: `POST /orders` → polling → `Completed`
- Assert: transições contêm os 5 estados esperados na ordem correta
- Usar `[Collection("Integration")]` para compartilhar a fixture
- Verificar: teste passa

**Dependências:** T4, T5, T6

---

## T8 — CompensationTests (T2, T3, T4 da spec)

**Status:** pending

- Criar `tests/IntegrationTests/Tests/CompensationTests.cs`
- Teste `PaymentFailure`: header `payment` → `Failed`, sem compensação no histórico
- Teste `InventoryFailure`: header `inventory` → `Failed`, `PaymentRefunding` no histórico
- Teste `ShippingFailure`: header `shipping` → `Failed`, `InventoryReleasing` + `PaymentRefunding` no histórico
- Verificar: 3 testes passam

**Dependências:** T4, T5

---

## T9 — IdempotencyTests (T5 da spec)

**Status:** pending

- Criar `tests/IntegrationTests/Tests/IdempotencyTests.cs`
- Teste: dois pedidos idênticos processados → estado final consistente, sem corrupção
- (ver D7 no design.md para escopo exato)
- Verificar: teste passa

**Dependências:** T4, T5

---

## T10 — ConcurrencyTests (T6, T7 da spec)

**Status:** pending

- Criar `tests/IntegrationTests/Tests/ConcurrencyTests.cs`
- Teste `WithLock`: 5 pedidos paralelos, estoque=2 → exatamente 2 `Completed`, 3 `Failed`, estoque final=0
- Teste `WithoutLock`: mesmo cenário, documenta resultado sem assertion de contagem exata
- Verificar: `WithLock` passa deterministicamente

**Dependências:** T4, T5, T6

---

## T11 — Verificação final

**Status:** pending

- `dotnet build tests/IntegrationTests/` passa sem warnings
- `dotnet test tests/IntegrationTests/ --no-build` executa (pode ser demorado, ~2-3min)
- Confirmar que T1–T6 passam e T7 passa (documentacional)

**Dependências:** T7, T8, T9, T10
