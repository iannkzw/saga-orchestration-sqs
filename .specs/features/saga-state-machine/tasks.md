# Tasks: saga-state-machine

**Feature:** saga-state-machine
**Milestone:** M2 - Saga Happy Path

## T1 - Adicionar pacotes EF Core ao SagaOrchestrator — DONE
- Adicionado `Microsoft.EntityFrameworkCore` (10.0.0-preview.3.25171.6) e `Npgsql.EntityFrameworkCore.PostgreSQL` (10.0.0-preview.3)

## T2 - Criar modelo de dominio (SagaState, SagaInstance, SagaStateTransition) — DONE
- `SagaOrchestrator/Models/SagaState.cs` — enum com 5 estados
- `SagaOrchestrator/Models/SagaInstance.cs` — entidade com TransitionTo()
- `SagaOrchestrator/Models/SagaStateTransition.cs` — historico de transicoes

## T3 - Criar SagaDbContext com configuracao EF Core — DONE
- `SagaOrchestrator/Data/SagaDbContext.cs` — DbSets, mapeamento snake_case, relacionamentos

## T4 - Registrar DbContext no DI e EnsureCreated no startup — DONE
- DbContext registrado com Npgsql no Program.cs
- EnsureCreated() chamado no startup

## T5 - Implementar SagaStateMachine (regras de transicao) — DONE
- `SagaOrchestrator/StateMachine/SagaStateMachine.cs` — TryAdvance() com mapa de transicoes

## T6 - Atualizar Worker para polling de replies e transicoes — DONE
- Polling das 3 filas de reply (payment, inventory, shipping)
- Deserializacao tipada, transicao via SagaStateMachine, envio do proximo comando
- Replies com falha logados (compensacao no M3)

## T7 - Criar endpoints de saga — DONE
- POST /sagas — cria saga, transiciona para PaymentProcessing, envia ProcessPayment
- GET /sagas/{id} — retorna estado atual e historico de transicoes
