# Feature: mt-readme

**Milestone:** M10 - Migração MassTransit
**Status:** PLANNED

## Objetivo

Atualizar o `README.md` raiz do repositório para refletir a arquitetura MassTransit, mantendo a estrutura atual de setup, exemplos e referências.

## Seções a Atualizar

### 1. Visão Geral / Diagrama de Arquitetura

Substituir diagrama de 5 serviços por 4 serviços. Adicionar menção ao MassTransit como framework de mensageria.

**Antes:**
```
OrderService → HTTP → SagaOrchestrator → SQS → PaymentService
                                       → SQS → InventoryService
                                       → SQS → ShippingService
```

**Depois:**
```
Client → HTTP → OrderService (MassTransit State Machine)
                    ↕ SQS (via MassTransit)
         PaymentService | InventoryService | ShippingService
```

### 2. Stack Tecnológico

Adicionar:
- `MassTransit 8.x` — framework de mensageria
- `MassTransit.AmazonSQS` — transporte SQS

### 3. Quick Start

Verificar que os comandos ainda funcionam:
```bash
git clone ...
docker compose up
curl -X POST http://localhost:5001/orders ...
```

Remover qualquer referência a `http://localhost:5000` (porta do SagaOrchestrator).

### 4. Exemplo de Happy Path

Atualizar response do `POST /orders` se o formato mudou. Atualizar `GET /orders/{id}` para mostrar campo `status`.

### 5. Cenários de Falha e Compensação

Atualizar para mostrar compensação via state machine declarativa. Pode mencionar o novo doc `docs/09-masstransit-state-machine.md`.

### 6. Seção de Scripts

Atualizar referências aos scripts atualizados (`happy-path-demo.sh`, `concurrent-saga-demo.sh`). Remover referência ao `init-sqs.sh` se foi removido.

### 7. Índice de Documentação

Adicionar links para os 2 novos docs:
- `docs/09-masstransit-state-machine.md`
- `docs/10-outbox-pattern.md`

### 8. Motivação da Migração (nova seção)

Adicionar seção breve "Por que MassTransit?" explicando a decisão de migrar do approach manual para o declarativo. Pode linkar para o guia de migração em `docs/masstransit-migration/`.

## O que NÃO mudar

- Estrutura geral do README (seções existentes mantidas)
- Tom educacional em português
- Links para documentação externa
- Badge de status se existir

## Critérios de Aceite

1. Nenhuma referência ao `SagaOrchestrator` como serviço separado
2. Diagrama de arquitetura com 4 serviços
3. Quick start funciona sem passos adicionais
4. Links para todos os 10 docs (`01` a `10`)
5. Seção "Por que MassTransit?" presente
