# Feature: saga-state-machine

**Milestone:** M2 - Saga Happy Path
**Status:** DONE

## Objetivo

Implementar a maquina de estados da saga no SagaOrchestrator com persistencia no PostgreSQL via EF Core + Npgsql. O orquestrador deve manter o estado de cada saga, registrar historico de transicoes e reagir a replies dos servicos para avancar o fluxo.

## Estados da Saga

```
Pending -> PaymentProcessing -> InventoryReserving -> ShippingScheduling -> Completed
```

Cada transicao e acionada por um reply de sucesso do servico correspondente:
- Saga criada -> estado `Pending`, envia `ProcessPayment`
- `PaymentReply(Success)` -> estado `InventoryReserving`, envia `ReserveInventory`
- `InventoryReply(Success)` -> estado `ShippingScheduling`, envia `ScheduleShipping`
- `ShippingReply(Success)` -> estado `Completed`

> Nota: estados de falha/compensacao serao tratados no M3.

## Modelo de Dados

### Tabela `sagas`

| Coluna | Tipo | Descricao |
|--------|------|-----------|
| id | UUID (PK) | SagaId |
| order_id | UUID | ID do pedido associado |
| current_state | VARCHAR(50) | Estado atual da saga |
| created_at | TIMESTAMP | Data de criacao |
| updated_at | TIMESTAMP | Ultima atualizacao |

### Tabela `saga_state_transitions`

| Coluna | Tipo | Descricao |
|--------|------|-----------|
| id | UUID (PK) | ID da transicao |
| saga_id | UUID (FK) | Referencia a saga |
| from_state | VARCHAR(50) | Estado anterior |
| to_state | VARCHAR(50) | Novo estado |
| triggered_by | VARCHAR(100) | Evento que causou a transicao |
| timestamp | TIMESTAMP | Momento da transicao |

## Componentes

### 1. SagaState (enum)
Enum com todos os estados possiveis: `Pending`, `PaymentProcessing`, `InventoryReserving`, `ShippingScheduling`, `Completed`.

### 2. SagaInstance (entity)
Entidade EF Core mapeada para tabela `sagas`. Contem metodo `TransitionTo(newState, triggeredBy)` que valida a transicao e cria o registro de historico.

### 3. SagaStateTransition (entity)
Entidade EF Core mapeada para tabela `saga_state_transitions`.

### 4. SagaDbContext
DbContext do EF Core com `DbSet<SagaInstance>` e `DbSet<SagaStateTransition>`. Configurado com Npgsql.

### 5. SagaStateMachine
Classe que encapsula as regras de transicao validas. Dado um estado atual, retorna o proximo estado e o comando a enviar.

### 6. Worker (atualizado)
Polling das filas de reply (payment-replies, inventory-replies, shipping-replies). Ao receber um reply de sucesso, usa SagaStateMachine para transicionar e despachar o proximo comando.

## Decisoes Tecnicas

- **EF Core no SagaOrchestrator:** DbContext fica no projeto SagaOrchestrator (nao no Shared) pois e especifico do orquestrador
- **Migrations via EnsureCreated:** Para PoC, usar `Database.EnsureCreated()` no startup ao inves de migrations formais
- **Snake_case no PostgreSQL:** Usar `.ToTable("sagas")` e `.HasColumnName("snake_case")` nas configuracoes do EF Core
- **Sem compensacao neste milestone:** Replies com `Success=false` serao logados mas nao tratados ate M3

## Criterios de Aceite

1. SagaOrchestrator persiste novas sagas no PostgreSQL com estado `Pending`
2. Worker faz polling das filas de reply e processa mensagens
3. Cada reply de sucesso transiciona a saga para o proximo estado
4. Historico de transicoes e registrado na tabela `saga_state_transitions`
5. Ao receber ShippingReply com sucesso, saga vai para `Completed`
6. Transicoes invalidas sao rejeitadas e logadas
