# State

## Decisions

- **2026-03-28:** Projeto inicializado com spec-driven workflow. Milestones M1-M4 definidos seguindo o PROMPT-INIT.md.
- **2026-03-28:** Feature infra-docker-compose implementada (T1-T6). Docker Compose, LocalStack, PostgreSQL, 5 servicos .NET placeholder com health checks.
- **2026-03-28:** "Multiplas sagas concorrentes" movido de out-of-scope para M5 com exemplo didatico. Adicionado doc sobre concorrencia no M4 (docs-didaticos).

## Blockers

_Nenhum no momento._

## Lessons Learned

- **LocalStack >=2026.3:** Requer `LOCALSTACK_ACKNOWLEDGE_ACCOUNT_REQUIREMENT=1` para rodar sem auth token (snooze temporario)
- **.NET 10 preview aspnet image:** Nao inclui `curl` nem `wget`. Necessario instalar `curl` via apt no Dockerfile para health checks do Docker Compose
- **.NET 10 csproj:** Precisa de `<ImplicitUsings>enable</ImplicitUsings>` para `WebApplication` funcionar sem `using` explicito

## Deferred Ideas

_Ver "Future Considerations" no ROADMAP.md._

## Preferences

- Documentacao em portugues
- Construcao incremental por milestones
- Usar subagentes para trabalho pesado, manter janela de tokens baixa
