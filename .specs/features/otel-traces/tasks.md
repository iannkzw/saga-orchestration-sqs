# otel-traces — Tasks

## T1 — Adicionar pacotes OTel no Shared.csproj
**Req:** R1.1
**Deps:** nenhuma
**Verificacao:** `dotnet restore` compila sem erro

Adicionar pacotes OpenTelemetry no Shared.csproj para que todos os servicos herdem via ProjectReference.

## T2 — Criar SqsTracePropagation helper
**Req:** R2.1, R2.2, R2.3
**Deps:** T1
**Verificacao:** Classe compila, metodos Inject/Extract implementados

Criar `Shared/Telemetry/SqsTracePropagation.cs` com inject/extract de W3C TraceContext via SQS message attributes.

## T3 — Criar SagaActivitySource
**Req:** R3.1, R3.2, R3.3
**Deps:** T1
**Verificacao:** Classe compila, factory methods retornam Activity com tags corretas

Criar `Shared/Telemetry/SagaActivitySource.cs` com ActivitySource + factory methods para spans padronizados.

## T4 — Criar AddSagaTracing extension method
**Req:** R1.2, R4.1, R4.2, R4.3, R5.1, R5.2
**Deps:** T1, T3
**Verificacao:** Extension method compila e registra OTel com console + OTLP opcional

Adicionar `AddSagaTracing(serviceName)` no `ServiceCollectionExtensions.cs`.

## T5 — Instrumentar SagaOrchestrator
**Req:** R1.3, R2.1, R2.2, R3.1, R3.2, R3.3
**Deps:** T2, T3, T4
**Verificacao:** Program.cs chama AddSagaTracing, Worker usa SagaActivitySource + SqsTracePropagation

Registrar OTel no Program.cs e instrumentar Worker.cs (send commands, process replies).

## T6 — Instrumentar OrderService
**Req:** R1.3, R5.1, R5.2
**Deps:** T4
**Verificacao:** Program.cs chama AddSagaTracing

Registrar OTel no Program.cs. Instrumentacao HTTP automatica via AspNetCore + HttpClient.

## T7 — Instrumentar PaymentService
**Req:** R1.3, R2.1, R2.2, R3.1, R3.2
**Deps:** T2, T3, T4
**Verificacao:** Worker usa SagaActivitySource + SqsTracePropagation nos handlers

Registrar OTel e instrumentar Worker (process command, send reply).

## T8 — Instrumentar InventoryService
**Req:** R1.3, R2.1, R2.2, R3.1, R3.2
**Deps:** T2, T3, T4
**Verificacao:** Worker usa SagaActivitySource + SqsTracePropagation nos handlers

Mesmo padrao do T7.

## T9 — Instrumentar ShippingService
**Req:** R1.3, R2.1, R2.2, R3.1, R3.2
**Deps:** T2, T3, T4
**Verificacao:** Worker usa SagaActivitySource + SqsTracePropagation nos handlers

Mesmo padrao do T7/T8.

## T10 — Atualizar ROADMAP.md e STATE.md
**Req:** -
**Deps:** T5-T9
**Verificacao:** ROADMAP marca otel-traces como DONE, STATE registra decisao

Atualizar documentacao do projeto.
