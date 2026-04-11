# Tasks: mt-messaging-infra

**Feature:** mt-messaging-infra
**Milestone:** M10 - Migração MassTransit

## Resumo

3 tarefas. Depende de `mt-event-contracts` (pacote MassTransit no Shared). A configuração final nos `Program.cs` é coordenada com `mt-program-config`.

---

## T1 — Adicionar pacotes MassTransit.AmazonSqs aos projetos — DONE

**Arquivos:** `*.csproj` de cada serviço

**O que fazer:**

Adicionar em cada `*.csproj`:
```xml
<PackageReference Include="MassTransit.AmazonSQS" Version="8.*" />
```

Verificar versão compatível com .NET 10 antes de fixar.

Os 4 serviços precisam do pacote:
- `src/OrderService/OrderService.csproj`
- `src/PaymentService/PaymentService.csproj`
- `src/InventoryService/InventoryService.csproj`
- `src/ShippingService/ShippingService.csproj`

**Verificação:** `dotnet restore` sem erros em todos os projetos.

---

## T2 — Criar `ConfigureSqsHost` extension method no Shared — DONE

**Arquivo:** `src/Shared/Extensions/MassTransitExtensions.cs`

**O que fazer:**

Criar extension method `ConfigureSqsHost(this IAmazonSqsBusFactoryConfigurator cfg, IConfiguration configuration)` conforme spec.md.

Variáveis de ambiente lidas:
- `AWS_SQS_SERVICE_URL` (default: `http://localstack:4566`)
- `AWS_SNS_SERVICE_URL` (default: `http://localstack:4566`)
- `AWS_ACCESS_KEY_ID` (default: `test`)
- `AWS_SECRET_ACCESS_KEY` (default: `test`)
- `AWS_REGION` (default: `us-east-1`)

**Verificação:** `dotnet build src/Shared/` compila com o novo extension method.

---

## T3 — Validar criação automática de filas no LocalStack — DEFERRED (mt-infra-docker)

**O que fazer:**

Após configurar MassTransit nos serviços (via `mt-program-config`), verificar que:

1. As filas SQS esperadas são criadas no LocalStack ao subir os contêineres
2. As DLQs (`*_error`) também são criadas
3. Os SNS topics correspondentes aos eventos são criados

Script de validação:
```bash
aws --endpoint-url=http://localhost:4566 sqs list-queues --region us-east-1
aws --endpoint-url=http://localhost:4566 sns list-topics --region us-east-1
```

**Verificação:** Todas as filas listadas na spec aparecem no output do `list-queues`.

---

## Dependências

```
mt-event-contracts (pacote MassTransit no Shared) → T1, T2
T1, T2 → T3 (validação após serviços estarem configurados)
mt-program-config (configura Program.cs com UsingAmazonSqs) precede T3
```
