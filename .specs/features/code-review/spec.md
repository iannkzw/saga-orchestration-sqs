# Spec: code-review

## Objetivo

Revisão de código completa dos milestones M1–M5. Identificar bugs, inconsistências,
riscos e oportunidades de melhoria *sem* implementar correções.

## Arquivos no Escopo

### Prioridade Alta
- `src/InventoryService/InventoryRepository.cs`
- `src/InventoryService/Worker.cs`
- `src/InventoryService/Program.cs`
- `scripts/concurrent-saga-demo.sh`
- `scripts/happy-path-demo.sh`
- `scripts/lib/common.sh`

### Prioridade Média
- `src/Shared/Idempotency/IdempotencyStore.cs`
- `src/Shared/Telemetry/SqsTracePropagation.cs`
- `src/Shared/Telemetry/SagaActivitySource.cs`
- `src/SagaOrchestrator/Worker.cs`
- `docker-compose.yml`

### Fora do Escopo
- PaymentService/Worker.cs (implementação estável, coberta em M3)
- ShippingService/Worker.cs (idem)
- OrderService/* (idem)
- Testes de integração (gerados como scaffolding, sem lógica de negócio)
- Documentação .md

## Critérios de Severidade

| Nível    | Critério |
|----------|----------|
| CRITICAL | Bug que causa corrupção de dados, deadlock, ou pânico em produção |
| HIGH     | Bug funcional provável em cenários reais ou perda de dados silenciosa |
| MEDIUM   | Comportamento não documentado, risco em edge case, degradação de performance |
| LOW      | Melhoria de robustez, log ausente, inconsistência menor |
| INFO     | Observação sobre design, dívida técnica, ou decisão questionável aceitável para PoC |

## Entregável

`.specs/features/code-review/REPORT.md` com findings organizados por arquivo e severidade.
