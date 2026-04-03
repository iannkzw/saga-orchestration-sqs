#!/usr/bin/env bash
# =============================================================================
# concurrent-saga-demo.sh
# Demonstracao de concorrencia entre sagas disputando o mesmo produto.
#
# Cenario: estoque inicial = 2 unidades, 5 pedidos simultaneos
# Resultado esperado COM lock:  2 Completed + 3 Failed (compensacao)
# Resultado esperado SEM lock:  imprevisivel (overbooking possivel)
#
# Uso:
#   bash scripts/concurrent-saga-demo.sh [--no-lock] [--pedidos N] [--estoque N]
#
# Opcoes:
#   --no-lock     Demonstra race condition (requer reconfig do inventory-service)
#   --pedidos N   Numero de pedidos simultaneos (padrao: 5)
#   --estoque N   Estoque inicial do PROD-001 (padrao: 2)
# =============================================================================

set -euo pipefail

if [[ "${BASH_VERSINFO[0]}" -lt 4 ]]; then
  echo "ERRO: requer Bash >= 4. No macOS: brew install bash"
  exit 1
fi

source "$(dirname "$0")/lib/common.sh"

NUM_PEDIDOS=5
ESTOQUE_INICIAL=2
NO_LOCK=false

# --- Parse de argumentos ---
while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-lock)  NO_LOCK=true; shift ;;
    --pedidos)  NUM_PEDIDOS="$2"; shift 2 ;;
    --estoque)  ESTOQUE_INICIAL="$2"; shift 2 ;;
    *) error "Opcao desconhecida: $1"; exit 1 ;;
  esac
done

if [[ "$NO_LOCK" == true ]]; then
  MODO_LOCK="SEM LOCK (race condition)"
else
  MODO_LOCK="COM LOCK (FOR UPDATE)"
fi

# --- Verificar dependencias ---
for cmd in curl jq; do
  if ! command -v "$cmd" &>/dev/null; then
    error "Dependencia ausente: $cmd"
    exit 1
  fi
done

if [[ "$NO_LOCK" == true ]] && ! command -v docker &>/dev/null; then
  error "Dependencia ausente: docker (necessario para verificar modo de lock)"
  exit 1
fi

# --- Verificar saude dos servicos ---
header "=== Verificando servicos ==="
check_health "OrderService"    "$ORDER_URL"
check_health "InventoryService" "$INVENTORY_URL"
check_health "SagaOrchestrator" "$ORCHESTRATOR_URL"

# --- Verificar / informar modo de lock ---
header "=== Modo: $MODO_LOCK ==="

if [[ "$NO_LOCK" == true ]]; then
  if ! docker logs saga-inventory-service 2>&1 | grep -qi "locking=sem lock"; then
    error "O InventoryService NAO esta no modo SEM LOCK."
    echo ""
    echo -e "  Para o modo sem lock, reinicie o inventory-service com:"
    echo -e "    ${BOLD}INVENTORY_LOCKING_ENABLED=false docker compose up -d inventory-service${NC}"
    echo -e "  Verifique no log: ${CYAN}\"locking=SEM LOCK (demonstracao de race condition)\"${NC}"
    echo -e "  Entao reexecute este script."
    echo ""
    exit 1
  fi
  warn "Modo SEM LOCK confirmado no container."
  warn "Com Task.WhenAll paralelo e sem FOR UPDATE, multiplas transacoes podem"
  warn "ler o mesmo estoque antes de qualquer UPDATE — causando overbooking."
else
  log "Modo COM LOCK: SELECT FOR UPDATE garante serializacao correta."
fi

# --- Resetar estoque (uma unica vez) ---
header "=== Resetando estoque ==="
reset_stock "$PRODUCT_ID" "$ESTOQUE_INICIAL"
STOCK_VERIFICADO=$(get_stock "$PRODUCT_ID")
success "Estoque de $PRODUCT_ID: $STOCK_VERIFICADO unidades"

# --- Disparar pedidos em paralelo ---
header "=== Disparando $NUM_PEDIDOS pedidos simultaneos para $PRODUCT_ID ==="
log "Cada pedido solicita 1 unidade | Estoque disponivel: $ESTOQUE_INICIAL"
log "Expectativa COM LOCK: $ESTOQUE_INICIAL Completed + $((NUM_PEDIDOS - ESTOQUE_INICIAL)) Failed"
echo ""

TMP_DIR=$(mktemp -d)
trap 'rm -rf "$TMP_DIR"' EXIT
declare -a PIDS

for i in $(seq 1 "$NUM_PEDIDOS"); do
  (
    curl -sf -X POST "$ORDER_URL/orders" \
      -H "Content-Type: application/json" \
      -d "{
        \"totalAmount\": 99.90,
        \"items\": [{
          \"productId\": \"$PRODUCT_ID\",
          \"quantity\": 1,
          \"unitPrice\": 99.90
        }]
      }" > "$TMP_DIR/order_$i.json" 2>/dev/null \
      || echo '{}' > "$TMP_DIR/order_$i.json"
  ) &
  PIDS+=($!)
  log "Pedido $i disparado (PID ${PIDS[-1]})"
done

log "Aguardando conclusao dos ${#PIDS[@]} pedidos..."
for pid in "${PIDS[@]}"; do
  wait "$pid" || true
done

echo ""
success "Todos os pedidos foram submetidos ao OrderService"

# --- Coletar IDs das sagas ---
header "=== Sagas criadas ==="
declare -a SAGA_IDS
declare -a ORDER_IDS

for i in $(seq 1 "$NUM_PEDIDOS"); do
  f="$TMP_DIR/order_$i.json"
  ORDER_ID=$(jq -r '.orderId // empty' "$f" 2>/dev/null || true)
  SAGA_ID=$(jq -r '.sagaId // empty' "$f" 2>/dev/null || true)
  if [[ -n "$ORDER_ID" ]]; then
    ORDER_IDS+=("$ORDER_ID")
    SAGA_IDS+=("$SAGA_ID")
    log "Pedido $i → orderId=$ORDER_ID | sagaId=$SAGA_ID"
  else
    warn "Pedido $i → falha na criacao (resposta invalida)"
    ORDER_IDS+=("")
    SAGA_IDS+=("")
  fi
done

# --- Poll das sagas ate estado terminal ---
header "=== Aguardando sagas atingirem estado terminal ==="
log "Polling a cada 2s por ate 90s..."
echo ""

declare -A SAGA_STATES
TIMEOUT=90
START_TIME=$SECONDS

while true; do
  PENDING=0
  for saga_id in "${SAGA_IDS[@]}"; do
    [[ -z "$saga_id" ]] && continue
    current="${SAGA_STATES[$saga_id]:-}"
    if [[ "$current" != "Completed" && "$current" != "Failed" ]]; then
      STATE=$(curl -sf "$ORCHESTRATOR_URL/sagas/$saga_id" 2>/dev/null | jq -r '.state // "Unknown"')
      SAGA_STATES["$saga_id"]="$STATE"
      if [[ "$STATE" != "Completed" && "$STATE" != "Failed" ]]; then
        PENDING=$((PENDING + 1))
      fi
    fi
  done

  [[ $PENDING -eq 0 ]] && break

  ELAPSED=$((SECONDS - START_TIME))
  if [[ $ELAPSED -ge $TIMEOUT ]]; then
    warn "Timeout apos ${TIMEOUT}s. $PENDING saga(s) ainda pendentes."
    break
  fi

  log "Ainda aguardando $PENDING saga(s)... (${ELAPSED}s)"
  sleep 2
done

# --- Resultado final ---
header "=== Resultado Final ==="

STOCK_FINAL=$(get_stock "$PRODUCT_ID")
COUNT_COMPLETED=0
COUNT_FAILED=0
COUNT_OTHER=0

for i in "${!SAGA_IDS[@]}"; do
  saga_id="${SAGA_IDS[$i]}"
  [[ -z "$saga_id" ]] && continue
  state="${SAGA_STATES[$saga_id]:-Timeout}"
  pedido_num=$((i + 1))

  case "$state" in
    Completed)
      COUNT_COMPLETED=$((COUNT_COMPLETED + 1))
      success "Saga $pedido_num ($saga_id): Completed"
      ;;
    Failed)
      COUNT_FAILED=$((COUNT_FAILED + 1))
      warn "Saga $pedido_num ($saga_id): Failed (compensacao executada)"
      ;;
    *)
      COUNT_OTHER=$((COUNT_OTHER + 1))
      error "Saga $pedido_num ($saga_id): $state (timeout ou erro)"
      ;;
  esac
done

echo ""
echo -e "${BOLD}--- Resumo ---${NC}"
echo -e "Modo:               ${BOLD}$MODO_LOCK${NC}"
echo -e "Pedidos disparados: $NUM_PEDIDOS"
echo -e "Estoque inicial:    $ESTOQUE_INICIAL"
echo -e "Estoque final:      ${BOLD}$STOCK_FINAL${NC}"
echo -e "Sagas Completed:    ${GREEN}${BOLD}$COUNT_COMPLETED${NC}"
echo -e "Sagas Failed:       ${YELLOW}${BOLD}$COUNT_FAILED${NC}"
[[ $COUNT_OTHER -gt 0 ]] && echo -e "Sagas em timeout:   ${RED}${BOLD}$COUNT_OTHER${NC}"
echo ""

# --- Diagnostico ---
if [[ "$NO_LOCK" == true ]]; then
  if [[ "$STOCK_FINAL" -lt 0 ]] 2>/dev/null; then
    error "OVERBOOKING DETECTADO! Estoque = $STOCK_FINAL (negativo)"
    error "Race condition em acao: multiplas transacoes aprovaram alem do estoque disponivel"
  elif [[ $COUNT_COMPLETED -gt $ESTOQUE_INICIAL ]]; then
    error "OVERBOOKING: $COUNT_COMPLETED pedidos aprovados com apenas $ESTOQUE_INICIAL em estoque"
  else
    warn "Race condition nao foi observada nesta execucao (resultado pode variar, tente novamente)"
  fi
else
  if [[ $COUNT_COMPLETED -eq $ESTOQUE_INICIAL && $COUNT_FAILED -eq $((NUM_PEDIDOS - ESTOQUE_INICIAL)) ]]; then
    success "Resultado CORRETO: $COUNT_COMPLETED pedidos aprovados (= estoque disponivel)"
    success "FOR UPDATE serializou o acesso — sem overbooking!"
  elif [[ $COUNT_COMPLETED -lt $ESTOQUE_INICIAL ]]; then
    warn "Menos pedidos completados que o esperado (possivel timeout ou servico lento)"
  fi
fi

# --- Cleanup ---
rm -rf "$TMP_DIR"

echo ""
log "Para ver logs do InventoryService:"
log "  docker logs saga-inventory-service --tail=50"
log ""
log "Para ver estado de uma saga especifica:"
log "  curl $ORCHESTRATOR_URL/sagas/<saga-id> | jq"
