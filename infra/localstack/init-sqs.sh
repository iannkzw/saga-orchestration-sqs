#!/bin/bash

# =============================================================================
# Script de inicialização das filas SQS no LocalStack
# Cria as filas principais e suas respectivas DLQs (Dead Letter Queues)
# com RedrivePolicy configurada (maxReceiveCount=3)
# =============================================================================

# URL base das filas no LocalStack
QUEUE_BASE_URL="http://sqs.us-east-1.localhost.localstack.cloud:4566/000000000000"

# Lista das filas principais a serem criadas
QUEUES=(
  "order-commands"
  "payment-commands"
  "payment-replies"
  "inventory-commands"
  "inventory-replies"
  "shipping-commands"
  "shipping-replies"
  "saga-commands"
)

# =============================================================================
# Função auxiliar: cria uma DLQ e a fila principal com RedrivePolicy
# =============================================================================
create_queue_with_dlq() {
  local QUEUE_NAME=$1

  # --- Criação da DLQ ---
  echo "Criando DLQ: ${QUEUE_NAME}-dlq..."
  awslocal sqs create-queue --queue-name "${QUEUE_NAME}-dlq"

  # Obtém o ARN da DLQ recém-criada
  DLQ_ARN=$(awslocal sqs get-queue-attributes \
    --queue-url "${QUEUE_BASE_URL}/${QUEUE_NAME}-dlq" \
    --attribute-names QueueArn \
    --query 'Attributes.QueueArn' \
    --output text)

  # --- Criação da fila principal com RedrivePolicy apontando para a DLQ ---
  echo "Criando fila principal: ${QUEUE_NAME}..."
  awslocal sqs create-queue \
    --queue-name "${QUEUE_NAME}" \
    --attributes "{\"RedrivePolicy\":\"{\\\"deadLetterTargetArn\\\":\\\"$DLQ_ARN\\\",\\\"maxReceiveCount\\\":\\\"3\\\"}\"}"

  echo "---"
}

# =============================================================================
# Criação de todas as filas
# =============================================================================
for QUEUE in "${QUEUES[@]}"; do
  create_queue_with_dlq "$QUEUE"
done

echo "Todas as filas foram criadas com sucesso!"
