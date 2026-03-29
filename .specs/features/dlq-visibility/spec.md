# dlq-visibility

**Milestone:** M3 - Compensacoes e Resiliencia
**Status:** DONE

## Objetivo

Fornecer visibilidade sobre mensagens que foram enviadas para Dead Letter Queues (DLQs) apos excederem o maxReceiveCount (3 tentativas). Permite consultar e reprocessar mensagens problematicas.

## Escopo

- Endpoint GET /dlq para listar mensagens de todas as 8 DLQs
- Endpoint POST /dlq/redrive para reenviar mensagem da DLQ para a fila original
- Constantes centralizadas em SqsConfig (AllDlqNames, DlqToOriginalQueue)

## Decisoes Tecnicas

- VisibilityTimeout=0 no GET /dlq para peek sem esconder mensagens
- Body parseado como JSON quando possivel, string quando nao
- Validacao de DLQ conhecida no redrive para evitar envio para filas arbitrarias
- Mapeamento DLQ->original via substring (remove sufixo "-dlq")
