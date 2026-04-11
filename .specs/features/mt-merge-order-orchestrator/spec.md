# Feature: mt-merge-order-orchestrator

**Milestone:** M10 - Migração MassTransit
**Status:** PLANNED

## Objetivo

Fundir o projeto `SagaOrchestrator` dentro do `OrderService`, eliminando o hop HTTP entre os dois serviços, o problema de `Order.Status` preso em "Processing" e reduzindo a infraestrutura de 5 para 4 contêineres. O `OrderService` passa a ser o ponto único de entrada e de orquestração da saga.

## Contexto

Atualmente o fluxo é:
```
Cliente → POST /orders → OrderService → HTTP POST /sagas → SagaOrchestrator
```

O orquestrador é um Worker Service separado que não tem visibilidade direta sobre o `OrderService`. Por isso, quando a saga termina (Completed ou Failed), o `Order.Status` no banco do `OrderService` não é atualizado de forma confiável (bug M9, agora resolvido via fila `order-status-updates`, mas com dual-write).

Com a fusão:
```
Cliente → POST /orders → OrderService (publica evento SagaStarted via MassTransit)
```

O `OrderService` hospeda a `OrderStateMachine` do MassTransit e tem acesso direto ao `OrderDbContext` para atualizar `Order.Status` na mesma transação da transição de estado da saga.

## Estrutura Alvo

```
src/
  OrderService/
    Api/              ← Minimal API (endpoints HTTP)
    Consumers/        ← Consumidores MassTransit (respostas dos serviços)
    StateMachine/     ← OrderStateMachine declarativa
    Data/             ← OrderDbContext (pedidos + estado saga unificados)
    Models/           ← Order, OrderSagaState
    Program.cs
```

O projeto `src/SagaOrchestrator/` será removido completamente (via feature `mt-cleanup`).

## Decisões Técnicas

- **Sem HTTP entre serviços internos:** A comunicação acontece 100% via SQS (MassTransit)
- **Contexto unificado:** `Order` e `OrderSagaInstance` vivem no mesmo `OrderDbContext`
- **Order.Status é atualizado nas callbacks da state machine:** sem dual-write, sem filas auxiliares
- **SagaOrchestrator project reference removida de tudo:** `Shared.csproj` não referencia mais o orquestrador

## Critérios de Aceite

1. `OrderService` compila e inicia sem referência ao projeto `SagaOrchestrator`
2. `POST /orders` cria o pedido e publica evento MassTransit no mesmo fluxo
3. Não existe mais chamada HTTP de `OrderService` para `SagaOrchestrator`
4. A estrutura de pastas do `OrderService` reflete a organização acima
5. `Order.Status` é atualizado diretamente pela state machine (sem fila auxiliar)
