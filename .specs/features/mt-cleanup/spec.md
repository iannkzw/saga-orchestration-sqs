# Feature: mt-cleanup

**Milestone:** M10 - Migração MassTransit
**Status:** PLANNED

## Objetivo

Remover o projeto `SagaOrchestrator` e todo o código morto gerado pela migração para MassTransit. Esta feature deve ser executada **após** `mt-program-config` estar validado e funcionando — é a etapa de limpeza final.

## O que Remover

### Projeto `SagaOrchestrator`
- Diretório `src/SagaOrchestrator/` completo
- Referência no `saga-orchestration.sln`
- `ProjectReference` em qualquer `.csproj` que ainda aponte para ele

### Código no `Shared` (contratos legados)
- `*Command.cs` antigos (`ProcessPaymentCommand`, `ReserveInventoryCommand`, etc.)
- `*Reply.cs` antigos (`PaymentReply`, `InventoryReply`, `ShippingReply`)
- `SagaStartedMessage.cs` ou similar
- `IIdempotencyStore.cs` (se não removido em `mt-idempotency`)

### Workers manuais (se não removidos nas features dos consumidores)
- `PaymentService/Worker.cs`
- `InventoryService/Worker.cs`
- `ShippingService/Worker.cs`

### Infraestrutura legada
- Filas SQS hardcodadas no `init-sqs.sh` (substituídas pelo MassTransit)
- Variáveis de ambiente obsoletas:
  - `SAGA_ORCHESTRATOR_URL`
  - `PAYMENT_COMMANDS_QUEUE`, `PAYMENT_REPLIES_QUEUE`
  - `INVENTORY_COMMANDS_QUEUE`, `INVENTORY_REPLIES_QUEUE`
  - `SHIPPING_COMMANDS_QUEUE`, `SHIPPING_REPLIES_QUEUE`
  - `ORDER_STATUS_UPDATES_QUEUE`
  - `INVENTORY_LOCKING_ENABLED` (depreciada — mantida como fallback apenas)

### Helpers obsoletos no Shared
- `SqsHelper.cs` ou equivalente (se substituído pelo MassTransit)
- `MessageSerializer.cs` / `SqsMessageExtensions.cs` (se substituídos)

## O que NÃO Remover

- `src/Shared/Contracts/Events/` — novos contratos MassTransit
- `src/Shared/Contracts/Commands/` — novos contratos MassTransit
- `src/Shared/Extensions/MassTransitExtensions.cs`
- `src/Shared/Extensions/ServiceCollectionExtensions.cs` (OTel)
- `src/Shared/Telemetry/` (OTel)
- `src/InventoryService/InventoryRepository.cs` — lógica de negócio mantida
- Endpoints HTTP dos serviços de domínio (reset, health)

## Checklist de Remoção

```
[ ] src/SagaOrchestrator/ removido
[ ] SagaOrchestrator removido do .sln
[ ] Contratos legados (*Command, *Reply) removidos do Shared
[ ] Workers manuais removidos dos 3 serviços de domínio
[ ] init-sqs.sh removido ou atualizado
[ ] Variáveis de ambiente obsoletas removidas do docker-compose.yml
[ ] SqsHelper/MessageSerializer legados removidos
[ ] dotnet build solução completa sem erros
[ ] docker compose up sem referências a serviços removidos
```

## Critérios de Aceite

1. `dotnet build` na solução completa sem erros ou warnings de referências mortas
2. `grep -r "SagaOrchestrator\|PaymentReply\|InventoryReply\|ShippingReply" src/` sem resultados (exceto em arquivos de documentação)
3. `docker compose up` sem serviço `saga-orchestrator`
4. Fluxo end-to-end ainda funciona após remoção
