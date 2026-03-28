# State

## Decisions

- **2026-03-28:** Projeto inicializado com spec-driven workflow. Milestones M1-M4 definidos seguindo o PROMPT-INIT.md.
- **2026-03-28:** Feature infra-docker-compose implementada (T1-T6). Docker Compose, LocalStack, PostgreSQL, 5 servicos .NET placeholder com health checks.
- **2026-03-28:** "Multiplas sagas concorrentes" movido de out-of-scope para M5 com exemplo didatico. Adicionado doc sobre concorrencia no M4 (docs-didaticos).
- **2026-03-28:** Feature project-skeleton implementada (T1-T8). Solution .NET com 6 projetos, Directory.Build.props, global.json, projeto Shared com contracts/replies/SqsConfig, Worker Services, smoke tests de conectividade SQS+PostgreSQL. M1 concluido.

## Blockers

_Nenhum no momento._

## Lessons Learned

- **LocalStack >=2026.3:** Requer `LOCALSTACK_ACKNOWLEDGE_ACCOUNT_REQUIREMENT=1` para rodar sem auth token (snooze temporario)
- **.NET 10 preview aspnet image:** Nao inclui `curl` nem `wget`. Necessario instalar `curl` via apt no Dockerfile para health checks do Docker Compose
- **.NET 10 csproj:** Precisa de `<ImplicitUsings>enable</ImplicitUsings>` para `WebApplication` funcionar sem `using` explicito
- **AWSSDK.SQS v4 preview:** Versao 4.0.0-preview.5 e necessaria para .NET 10 — versao 3.x nao e compativel
- **Dockerfile com Shared:** Build context precisa ser raiz do repo (nao src/Service) para copiar Directory.Build.props e Shared

## Deferred Ideas

_Ver "Future Considerations" no ROADMAP.md._

## Preferences

- Documentacao em portugues
- Construcao incremental por milestones
- Usar subagentes para trabalho pesado, manter janela de tokens baixa
