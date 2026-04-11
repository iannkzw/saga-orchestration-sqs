# Feature: mt-codebase-docs

**Milestone:** M10 - Migração MassTransit
**Status:** PLANNED

## Objetivo

Criar ou atualizar os 7 arquivos de documentação de codebase (`ARCHITECTURE.md`, arquivos `README.md` por serviço, etc.) para refletir a nova estrutura MassTransit. Documentação voltada para desenvolvedores que vão trabalhar no código, não para leitores externos.

## Documentos de Codebase

### 1. `ARCHITECTURE.md` (raiz ou `docs/`)

Documento de referência técnica da arquitetura interna:
- Estrutura de projetos/namespaces
- Fluxo de mensagens (eventos, comandos, correlação)
- Dependências entre projetos
- Decisões arquiteturais (ADRs inline ou links para `docs/`)

### 2. `src/OrderService/README.md`

Documentação do `OrderService`:
- Responsabilidades do serviço
- Estrutura de pastas (`Api/`, `Consumers/`, `StateMachine/`, `Data/`, `Models/`)
- Como a state machine funciona localmente
- Endpoints HTTP disponíveis
- Variáveis de ambiente

### 3. `src/PaymentService/README.md`

Documentação do `PaymentService`:
- Responsabilidades
- Consumidores: `ProcessPaymentConsumer`, `CancelPaymentConsumer`
- Lógica de pagamento simulada
- Variáveis de ambiente

### 4. `src/InventoryService/README.md`

Documentação do `InventoryService`:
- Responsabilidades
- Consumidores: `ReserveInventoryConsumer`, `CancelInventoryConsumer`
- Modos de locking: `INVENTORY_LOCKING_MODE` (pessimistic, optimistic, none)
- Endpoints HTTP disponíveis (reset, health)

### 5. `src/ShippingService/README.md`

Documentação do `ShippingService`:
- Responsabilidades
- Consumidor: `ScheduleShippingConsumer`
- Lógica de entrega simulada
- Variáveis de ambiente

### 6. `src/Shared/README.md`

Documentação do projeto `Shared`:
- O que está no Shared (contratos, extensões, telemetria)
- Como usar `AddSagaTracing`
- Como usar `ConfigureSqsHost`
- Contratos: lista de eventos e comandos disponíveis

### 7. `docs/CONTRIBUTING.md` (ou `CONTRIBUTING.md` na raiz)

Guia para contribuidores:
- Como rodar localmente
- Como adicionar um novo serviço
- Como adicionar um novo evento/comando
- Convenções de nomenclatura (snake_case no DB, PascalCase no C#, pt-BR em docs)

## Padrão de README por Serviço

```markdown
# NomeDoServico

## Responsabilidade

[1-2 parágrafos]

## Estrutura

```
src/NomeDoServico/
  Consumers/   - consumidores MassTransit
  Data/        - DbContext (se aplicável)
  Models/      - entidades
  Program.cs
```

## Endpoints

| Método | Path | Descrição |
|--------|------|-----------|

## Variáveis de Ambiente

| Variável | Padrão | Descrição |
|----------|--------|-----------|

## Desenvolvimento Local

```bash
docker compose up nome-do-servico
```
```

## Critérios de Aceite

1. Os 7 arquivos de codebase docs existem
2. Nenhum referencia o `SagaOrchestrator` como serviço separado
3. `src/OrderService/README.md` descreve a estrutura de pastas correta
4. `src/Shared/README.md` lista todos os contratos de eventos e comandos
5. `ARCHITECTURE.md` tem diagrama de dependências entre projetos
