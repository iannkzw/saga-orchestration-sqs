# Tasks: order-api

**Feature:** order-api
**Milestone:** M2 - Saga Happy Path

## T1 - Adicionar pacotes EF Core ao OrderService — DONE
- Adicionado `Microsoft.EntityFrameworkCore` (10.0.0-preview.3.25171.6) e `Npgsql.EntityFrameworkCore.PostgreSQL` (10.0.0-preview.3)

## T2 - Criar modelo de dominio (OrderStatus, Order) — DONE
- `OrderService/Models/OrderStatus.cs` — enum com 4 estados (Pending, Processing, Completed, Failed)
- `OrderService/Models/Order.cs` — entidade com Id, SagaId, TotalAmount, ItemsJson, Status, timestamps

## T3 - Criar OrderDbContext com configuracao EF Core — DONE
- `OrderService/Data/OrderDbContext.cs` — DbSet, mapeamento snake_case, string conversion para Status

## T4 - Registrar DbContext e HttpClient no DI, EnsureCreated no startup — DONE
- DbContext registrado com Npgsql no Program.cs
- Named HttpClient "SagaOrchestrator" com base URL via SAGA_ORCHESTRATOR_URL
- EnsureCreated() chamado no startup
- SAGA_ORCHESTRATOR_URL adicionada ao docker-compose.yml (http://saga-orchestrator:8080)

## T5 - Implementar POST /orders — DONE
- Cria Order, persiste no PostgreSQL com status Pending
- Chama POST /sagas no orquestrador via HttpClient
- Atualiza Order com SagaId e status Processing
- Retorna 201 com orderId, sagaId, status

## T6 - Implementar GET /orders/{id} — DONE
- Busca Order no PostgreSQL, retorna 404 se nao existe
- Chama GET /sagas/{sagaId} no orquestrador para obter estado da saga
- Retorna pedido com items deserializados e saga data (graceful se falhar)
