# Feature: order-api

**Milestone:** M2 - Saga Happy Path
**Status:** IN PROGRESS

## Objetivo

Implementar a API de pedidos no OrderService com persistencia no PostgreSQL via EF Core + Npgsql. O endpoint POST /orders cria um pedido, persiste no banco e inicia uma saga chamando POST /sagas do orquestrador via HTTP. O endpoint GET /orders/{id} retorna o estado atual do pedido e consulta o estado da saga no orquestrador.

## Endpoints

### POST /orders

Cria um novo pedido e inicia a saga correspondente.

**Request body:**
```json
{
  "totalAmount": 150.00,
  "items": [
    { "productId": "PROD-001", "quantity": 2 }
  ]
}
```

**Fluxo:**
1. Gera OrderId (UUID)
2. Persiste Order no PostgreSQL com status `Pending`
3. Chama POST /sagas no SagaOrchestrator via HTTP (passa OrderId, TotalAmount, Items)
4. Atualiza Order com SagaId retornado pelo orquestrador
5. Retorna 201 Created com OrderId e SagaId

### GET /orders/{id}

Retorna o pedido e o estado atual da saga.

**Fluxo:**
1. Busca Order no PostgreSQL
2. Chama GET /sagas/{sagaId} no SagaOrchestrator via HTTP
3. Retorna pedido com estado da saga embarcado

## Modelo de Dados

### Tabela `orders`

| Coluna | Tipo | Descricao |
|--------|------|-----------|
| id | UUID (PK) | OrderId |
| saga_id | UUID (nullable) | SagaId retornado pelo orquestrador |
| total_amount | DECIMAL | Valor total do pedido |
| items_json | TEXT | Items serializados como JSON |
| status | VARCHAR(50) | Status do pedido (Pending, Processing, Completed, Failed) |
| created_at | TIMESTAMP | Data de criacao |
| updated_at | TIMESTAMP | Ultima atualizacao |

## Componentes

### 1. Order (entity)
Entidade EF Core mapeada para tabela `orders`.

### 2. OrderStatus (enum)
`Pending`, `Processing`, `Completed`, `Failed`.

### 3. OrderDbContext
DbContext do EF Core com `DbSet<Order>`. Configurado com Npgsql, snake_case.

### 4. Endpoints (Minimal API)
`POST /orders` e `GET /orders/{id}` no Program.cs usando Minimal API.

### 5. HttpClient para SagaOrchestrator
Named HttpClient configurado via DI para chamar o orquestrador (base URL via config/env).

## Decisoes Tecnicas

- **HTTP para iniciar saga:** O OrderService chama POST /sagas do orquestrador via HTTP (nao via SQS). Isso simplifica o fluxo sincrono de criacao e retorna o SagaId imediatamente ao cliente.
- **EF Core no OrderService:** DbContext fica no projeto OrderService (especifico do servico)
- **EnsureCreated:** Mesma abordagem do SagaOrchestrator — sem migrations formais para PoC
- **Snake_case:** Consistente com o padrao do SagaOrchestrator

## Criterios de Aceite

1. POST /orders persiste pedido no PostgreSQL com status `Pending`
2. POST /orders chama POST /sagas no orquestrador e recebe SagaId
3. Order e atualizado com SagaId apos criacao da saga
4. GET /orders/{id} retorna pedido com estado da saga
5. GET /orders/{id} retorna 404 se pedido nao existe
6. OrderService sobe com EnsureCreated sem erros
