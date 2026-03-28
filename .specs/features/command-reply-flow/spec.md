# Feature: Command-Reply Flow

**Milestone:** M2 - Saga Happy Path
**Status:** In Progress

## Objetivo

Implementar o fluxo command-reply nos tres servicos participantes (Payment, Inventory, Shipping). Cada servico faz polling da sua fila de comandos, processa o comando recebido (simulacao), e envia um reply de sucesso para a fila de respostas correspondente. O orquestrador ja esta pronto para consumir essas replies e transicionar a saga.

## Contexto

- O SagaOrchestrator ja envia comandos tipados (`ProcessPayment`, `ReserveInventory`, `ScheduleShipping`) para filas SQS dedicadas
- O SagaOrchestrator ja faz polling das filas de reply e transiciona estados
- Os Workers dos 3 servicos existem como placeholders (apenas log + delay)
- Contracts (commands e replies) ja existem no projeto Shared
- `SqsConfig` ja define os nomes das filas

## Requisitos Funcionais

### RF-1: PaymentService processa ProcessPayment
- Worker faz polling da fila `payment-commands`
- Deserializa `ProcessPayment` do body da mensagem SQS
- Simula processamento (log + delay curto)
- Envia `PaymentReply` com `Success=true` e `TransactionId` gerado para fila `payment-replies`
- Deleta mensagem da fila apos processamento
- Correlation via `SagaId` preservado no reply

### RF-2: InventoryService processa ReserveInventory
- Worker faz polling da fila `inventory-commands`
- Deserializa `ReserveInventory` do body da mensagem SQS
- Simula reserva de estoque (log + delay curto)
- Envia `InventoryReply` com `Success=true` e `ReservationId` gerado para fila `inventory-replies`
- Deleta mensagem da fila apos processamento
- Correlation via `SagaId` preservado no reply

### RF-3: ShippingService processa ScheduleShipping
- Worker faz polling da fila `shipping-commands`
- Deserializa `ScheduleShipping` do body da mensagem SQS
- Simula agendamento de envio (log + delay curto)
- Envia `ShippingReply` com `Success=true` e `TrackingNumber` gerado para fila `shipping-replies`
- Deleta mensagem da fila apos processamento
- Correlation via `SagaId` preservado no reply

## Requisitos Nao-Funcionais

- **Padrao consistente:** Os 3 workers seguem a mesma estrutura (poll -> deserialize -> process -> reply -> delete)
- **Logging estruturado:** Cada etapa loga SagaId, nome do comando e resultado
- **Resiliencia basica:** Try/catch no loop principal — erros sao logados mas nao derrubam o worker
- **PoC-friendly:** Simulacao com delay curto (100-500ms), sem logica de negocio real

## Decisoes de Design

- **Sem idempotencia neste milestone:** Idempotency sera implementada no M3
- **Apenas happy path:** Replies sempre com `Success=true` — falhas serao tratadas no M3
- **Injecao de dependencia:** `IAmazonSQS` ja registrado via `AddSagaConnectivity` — reusar
- **Delay simulado:** 200ms para simular processamento sem impactar tempo de teste

## Validacao

- `docker compose up` sobe todos os servicos
- `POST /sagas` inicia saga que transiciona ate `Completed`
- `GET /sagas/{id}` mostra todas as transicoes (Pending -> PaymentProcessing -> InventoryReserving -> ShippingScheduling -> Completed)
- Logs dos 3 servicos mostram processamento dos comandos
