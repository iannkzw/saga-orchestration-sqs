# State

> Decisions de M1–M8 arquivadas em [STATE-ARCHIVE.md](STATE-ARCHIVE.md)

## Decisions

- **2026-04-11:** Feature mt-merge-order-orchestrator concluida. OrderService absorveu a responsabilidade de orquestracao: HTTP hop para SagaOrchestrator removido, Worker.cs (polling order-status-updates) removido, endpoints movidos para Api/OrderEndpoints.cs, OrderDbContext unificado com DbSet<OrderSagaInstance>, MassTransit adicionado (in-memory por ora — SQS transport vem em mt-messaging-infra). Evento OrderPlaced criado em Shared/Contracts/Events/. Build compila com 0 erros.
- **2026-04-04:** Milestone M10 (Migração MassTransit) criada no ROADMAP com 18 features. Decisao arquitetural: fundir OrderService + SagaOrchestrator em um unico OrderService. Justificativa: com MassTransit a state machine e declarativa (~1 classe), nao justifica servico dedicado; elimina HTTP hop anti-pattern (POST /sagas); resolve bug Order.Status preso em Processing (M9 tornado obsoleto); Order e Saga sao do mesmo bounded context (order fulfillment). Resultado: 4 microsservicos independentes (OrderService, PaymentService, InventoryService, ShippingService) em vez de 5. Escopo total: ~900+ linhas eliminadas, 9+ filas SQS removidas, dual-write resolvido, docs reescritos (8 existentes + 2 novos), 7 docs codebase.

## Blockers

_Nenhum no momento._

## Lessons Learned

- **LocalStack >=2026.3:** Requer `LOCALSTACK_ACKNOWLEDGE_ACCOUNT_REQUIREMENT=1` para rodar sem auth token (snooze temporario)
- **.NET 10 preview aspnet image:** Nao inclui `curl` nem `wget`. Necessario instalar `curl` via apt no Dockerfile para health checks do Docker Compose
- **.NET 10 csproj:** Precisa de `<ImplicitUsings>enable</ImplicitUsings>` para `WebApplication` funcionar sem `using` explicito
- **AWSSDK.SQS v4 preview:** Versao 4.0.0-preview.5 e necessaria para .NET 10 — versao 3.x nao e compativel
- **EF Core 10 preview:** Usar 10.0.0-preview.3.25171.6. Npgsql.EFCore 10.0.0-preview.3 e compativel
- **Dockerfile com Shared:** Build context precisa ser raiz do repo (nao src/Service) para copiar Directory.Build.props e Shared
- **OpenTelemetry 1.15.0 + .NET 10:** OTel 1.15.0 depende de Microsoft.Extensions 10.0.0 (estavel). Versoes preview causam NU1605
- **SQS trace propagation manual:** AWSSDK.SQS nao tem instrumentacao OTel nativa. Necessario inject/extract manual de traceparent/tracestate via MessageAttributes
- **Task.WhenAll no SQS worker:** Para expor race conditions, o worker precisa processar mensagens do mesmo batch em paralelo
- **Delay artificial como janela TOCTOU:** Task.Delay(150ms) entre SELECT e UPDATE (modo sem lock) garante que multiplas transacoes paralelas vejam o mesmo valor
- **inventory_reservations como ponte de compensacao:** Armazenar reservation_id permite que compensacao restaure exatamente o estoque correto

## Deferred Ideas

_Ver "Future Considerations" no ROADMAP.md._

## Preferences

- Documentacao em portugues
- Construcao incremental por milestones
- Usar subagentes para trabalho pesado, manter janela de tokens baixa
