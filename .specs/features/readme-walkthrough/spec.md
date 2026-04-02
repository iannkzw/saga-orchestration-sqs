# Spec: readme-walkthrough

## Audiência-Alvo

Desenvolvedor que acabou de clonar o repositório pela primeira vez. Conhece Docker e HTTP básico, mas não sabe nada sobre o projeto. Objetivo: conseguir rodar e entender a demo em menos de 10 minutos.

## Requisitos

### R1 — Visão Geral
O README deve apresentar o projeto em até 3 parágrafos curtos cobrindo:
- O que é (PoC de Saga Orchestration com .NET + SQS + PostgreSQL)
- Objetivo didático (demonstrar padrões: happy path, compensação, idempotência, DLQ, concorrência, traces)
- Stack de tecnologias

**Critério de aceite:** Leitor entende em menos de 2 minutos o que o projeto faz e para que serve.

---

### R2 — Pré-requisitos
Lista clara dos pré-requisitos com versões mínimas recomendadas:
- Docker (com Docker Compose plugin)
- bash + curl + jq (para os scripts de demo)

**Critério de aceite:** Leitor sabe o que precisa instalar antes de rodar qualquer comando.

---

### R3 — Setup Rápido
Sequência mínima de comandos para subir o ambiente e verificar que está saudável:
1. `git clone` + `cd`
2. `docker compose up -d`
3. Verificação de health dos 7 serviços (localstack, postgres, 5 .NET services)
4. Saída esperada dos health checks

**Critério de aceite:** Leitor consegue subir o ambiente completo sem abrir nenhum outro arquivo.

---

### R4 — Cenário Happy Path
Exemplo funcional de `POST /orders` via curl com:
- Comando completo pronto para copiar
- Saída JSON esperada (orderId, sagaId)
- Comando para verificar o estado final da saga (`Completed`)
- Transições esperadas listadas

**Critério de aceite:** Leitor consegue executar o happy path completo com copy-paste.

---

### R5 — Cenário de Falha e Compensação
Exemplo de uso do header `X-Simulate-Failure` com:
- Os 3 valores possíveis: `payment`, `inventory`, `shipping`
- Exemplo concreto de curl com o header
- Estado final esperado (`Failed`)
- Explicação de qual cascata de compensação é acionada por cada falha

**Critério de aceite:** Leitor entende como provocar uma falha e o que esperar de retorno.

---

### R6 — Script de Happy Path Automatizado
Referência ao `scripts/happy-path-demo.sh` com:
- Comando para executar
- O que o script faz (4 cenários sequenciais)
- Saída esperada de sucesso

**Critério de aceite:** Leitor sabe que existe o script e como usá-lo.

---

### R7 — Cenário de Concorrência
Referência ao `scripts/concurrent-saga-demo.sh` com:
- Comando padrão e opções `--no-lock`, `--pedidos N`, `--estoque N`
- Resultado esperado com lock: 2 Completed + 3 Failed
- Resultado esperado sem lock: overbooking (race condition)

**Critério de aceite:** Leitor entende como demonstrar concorrência e o efeito do SELECT FOR UPDATE.

---

### R8 — DLQ Visibility
Exemplos de curl para:
- `GET /dlq` (listar mensagens das DLQs)
- `POST /dlq/redrive` (reenviar mensagem para fila original)

**Critério de aceite:** Leitor sabe como inspecionar e reprocessar mensagens problemáticas.

---

### R9 — Estrutura do Projeto
Árvore de diretórios comentada cobrindo:
- `src/` — 5 serviços + Shared
- `docs/` — documentação didática
- `scripts/` — scripts de demo
- `infra/` — LocalStack + PostgreSQL init

**Critério de aceite:** Leitor sabe onde encontrar cada componente sem explorar o repo manualmente.

---

### R10 — Tabela de Portas dos Serviços
Tabela com: nome do serviço, porta, URL de health check.
Cobrir: LocalStack (4566), PostgreSQL (5432), OrderService (5001), SagaOrchestrator (5002), PaymentService (5003), InventoryService (5004), ShippingService (5005).

**Critério de aceite:** Leitor tem referência rápida para acessar qualquer serviço.

---

### R11 — Links para Documentação Didática
Sumário dos 8 documentos em `docs/` com:
- Link relativo para cada arquivo
- Descrição de uma linha do que cada documento cobre

**Critério de aceite:** Leitor sabe qual documento ler para aprofundar cada tópico.
