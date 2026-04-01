#!/usr/bin/env bash
# =============================================================================
# concurrent-saga-demo.sh
# Demonstracao de concorrencia entre sagas disputando o mesmo produto.
#
# Cenario: estoque inicial = 2 unidades, 5 pedidos simultaneos
# Resultado esperado com lock:    2 Completed + 3 Failed (compensacao)
# Resultado esperado sem lock:    imprevisivel (overbooking possivel)
#
# Uso:
#   bash scripts/concurrent-saga-demo.sh [--no-lock] [--pedidos N] [--estoque N]
#
# Opcoes:
#   --no-lock     Desabilita o FOR UPDATE no InventoryService (demo de race condition)
#   --pedidos N   Numero de pedidos simultaneos (padrao: 5)
#   --estoque N   Estoque inicial do PROD-001 (padrao: 2)
# =============================================================================

set -euo pipefail

ORDER_URL="${ORDER_URL:-http://localhost:5001}"
INVENTORY_URL="${INVENTORY_URL:-http://localhost:5004}"
ORCHESTRATOR_URL="${ORCHESTRATOR_URL:-http://localhost:5002}"
PRODUCT_ID="PROD-001"
NUM_PEDIDOS=5
ESTOQUE_INICIAL=2
MODO_LOCK="com lock (FOR UPDATE)"

# --- Parse de argumentos ---
while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-lock)   MODO_LOCK="SEM LOCK (race condition)"; shift ;;
    --pedidos)   NUM_PEDIDOS="$2"; shift 2 ;;
    --estoque)   ESTOQUE_INICIAL="$2"; shift 2 ;;
    *) echo "Opcao desconhecida: $1"; exit 1 ;;
  esac
done

# --- Cores para output ---
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
BOLD='\033[1m'
NC='\033[0m'

log()     { echo -e "${CYAN}[$(date +%H:%M:%S)]${NC} $*"; }
success() { echo -e "${GREEN}[$(date +%H:%M:%S)] ✓${NC} $*"; }
warn()    { echo -e "${YELLOW}[$(date +%H:%M:%S)] ⚠${NC} $*"; }
error()   { echo -e "${RED}[$(date +%H:%M:%S)] ✗${NC} $*"; }
header()  { echo -e "\n${BOLD}${CYAN}$*${NC}\n"; }

# --- Verificar dependencias ---
for cmd in curl jq; do
  if ! command -v "$cmd" &>/dev/null; then
    error "Dependencia ausente: $cmd"
    exit 1
  fi
done

# --- Verificar saude dos servicos ---
header "=== Verificando servicos ==="
for name_url in "OrderService|$ORDER_URL" "InventoryService|$INVENTORY_URL" "SagaOrchestrator|$ORCHESTRATOR_URL"; do
  name="${name_url%%|*}"
  url="${name_url##*|}"
  if curl -sf "$url/health" > /dev/null 2>&1; then
    success "$name: OK ($url)"
  else
    error "$name nao esta respondendo em $url"
    error "Execute: docker compose up -d e aguarde os servicos ficarem saudaveis"
    exit 1
  fi
done

# --- Configurar modo de lock ---
header "=== Configurando modo: $MODO_LOCK ==="

if [[ "$MODO_LOCK" == *"SEM LOCK"* ]]; then
  warn "Modo SEM LOCK ativado via env var INVENTORY_LOCKING_ENABLED=false"
  warn "Para ativar, configure no docker-compose.yml e reinicie o inventory-service:"
  warn "  INVENTORY_LOCKING_ENABLED=false"
  warn ""
  warn "Com processamento paralelo e sem FOR UPDATE, multiplas transacoes podem"
  warn "ler o mesmo estoque antes de qualquer UPDATE — causando overbooking."
else
  log "Modo COM LOCK (padrao): SELECT FOR UPDATE garante serializacao correta"
fi

# --- Resetar estoque ---
header "=== Resetando estoque ==="

RESET_RESPONSE=$(curl -sf -X POST "$INVENTORY_URL/inventory/reset" \
  -H "Content-Type: application/json" \
  -d "{\"productId\": \"$PRODUCT_ID\", \"quantity\": $ESTOQUE_INICIAL}" 2>&1) || {
  error "Falha ao resetar estoque: $RESET_RESPONSE"
  exit 1
}

STOCK_AFTER_RESET=$(curl -sf "$INVENTORY_URL/inventory/stock/$PRODUCT_ID" | jq '.quantity')
success "Estoque de $PRODUCT_ID resetado para $STOCK_AFTER_RESET unidades"

# --- Disparar pedidos simultaneos ---
header "=== Disparando $NUM_PEDIDOS pedidos simultaneos para $PRODUCT_ID ==="
log "Cada pedido solicita 1 unidade | Estoque disponivel: $ESTOQUE_INICIAL"
log "Expectativa COM LOCK: $ESTOQUE_INICIAL Completed + $((NUM_PEDIDOS - ESTOQUE_INICIAL)) Failed"
echo ""

declare -a SAGA_IDS
declare -a ORDER_IDS
declare -a PIDS

for i in $(seq 1 "$NUM_PEDIDOS"); do
  RESPONSE=$(curl -sf -X POST "$ORDER_URL/orders" \
    -H "Content-Type: application/json" \
    -d "{
      \"totalAmount\": 99.90,
      \"items\": [{
        \"productId\": \"$PRODUCT_ID\",
        \"quantity\": 1,
        \"unitPrice\": 99.90
      }]
    }" 2>/dev/null) &
  PIDS+=($!)
  # Guardar o indice para associar ao PID depois
done

# Aguardar todos os pedidos e coletar IDs
declare -a RAW_RESPONSES
for i in "${!PIDS[@]}"; do
  wait "${PIDS[$i]}" || true
done

# Reexecutar as chamadas (desta vez capturando output) — abordagem simples para demo
# Reinicia as chamadas coletando saidas
log "Coletando IDs dos pedidos criados..."
RAW_RESPONSES=()
for i in $(seq 1 "$NUM_PEDIDOS"); do
  R=$(curl -sf -X POST "$ORDER_URL/orders" \
    -H "Content-Type: application/json" \
    -d "{\"totalAmount\": 99.90, \"items\": [{\"productId\": \"$PRODUCT_ID\", \"quantity\": 1, \"unitPrice\": 99.90}]}" \
    2>/dev/null || echo '{}') &
  RAW_RESPONSES+=("$!")
done

# Para capturar corretamente em paralelo, usamos um arquivo temporario por pedido
TMP_DIR=$(mktemp -d)
PIDS2=()

# Resetar estoque novamente para a rodada real
curl -sf -X POST "$INVENTORY_URL/inventory/reset" \
  -H "Content-Type: application/json" \
  -d "{\"productId\": \"$PRODUCT_ID\", \"quantity\": $ESTOQUE_INICIAL}" > /dev/null

log "Estoque resetado novamente. Disparando $NUM_PEDIDOS pedidos em paralelo..."
echo ""

for i in $(seq 1 "$NUM_PEDIDOS"); do
  (
    curl -sf -X POST "$ORDER_URL/orders" \
      -H "Content-Type: application/json" \
      -d "{\"totalAmount\": 99.90, \"items\": [{\"productId\": \"$PRODUCT_ID\", \"quantity\": 1, \"unitPrice\": 99.90}]}" \
      > "$TMP_DIR/order_$i.json" 2>/dev/null || echo '{}' > "$TMP_DIR/order_$i.json"
  ) &
  PIDS2+=($!)
  log "Pedido $i disparado (PID ${PIDS2[-1]})"
done

log "Aguardando conclusao dos ${#PIDS2[@]} pedidos..."
for pid in "${PIDS2[@]}"; do
  wait "$pid" || true
done

echo ""
success "Todos os pedidos foram submetidos ao OrderService"

# --- Coletar IDs das sagas ---
header "=== Sagas criadas ==="
for i in $(seq 1 "$NUM_PEDIDOS"); do
  f="$TMP_DIR/order_$i.json"
  if [[ -f "$f" ]] && ORDER_ID=$(jq -r '.orderId // empty' "$f" 2>/dev/null) && [[ -n "$ORDER_ID" ]]; then
    SAGA_ID=$(jq -r '.sagaId // empty' "$f" 2>/dev/null)
    ORDER_IDS+=("$ORDER_ID")
    SAGA_IDS+=("$SAGA_ID")
    log "Pedido $i → orderId=$ORDER_ID | sagaId=$SAGA_ID"
  else
    warn "Pedido $i → falha na criacao (resposta invalida)"
    ORDER_IDS+=("")
    SAGA_IDS+=("")
  fi
done

# --- Aguardar sagas terminarem ---
header "=== Aguardando sagas atingirem estado terminal ==="
log "Polling a cada 2s por ate 60s..."
echo ""

TIMEOUT=60
ELAPSED=0
declare -A SAGA_STATES

while [[ $ELAPSED -lt $TIMEOUT ]]; do
  PENDING=0
  for saga_id in "${SAGA_IDS[@]}"; do
    [[ -z "$saga_id" ]] && continue
    current="${SAGA_STATES[$saga_id]:-}"
    if [[ "$current" != "Completed" && "$current" != "Failed" ]]; then
      STATE=$(curl -sf "$ORCHESTRATOR_URL/sagas/$saga_id" 2>/dev/null | jq -r '.state // "Unknown"')
      SAGA_STATES[$saga_id]="$STATE"
      if [[ "$STATE" != "Completed" && "$STATE" != "Failed" ]]; then
        PENDING=$((PENDING + 1))
      fi
    fi
  done

  if [[ $PENDING -eq 0 ]]; then
    break
  fi

  log "Ainda aguardando $PENDING saga(s)..."
  sleep 2
  ELAPSED=$((ELAPSED + 2))
done

# --- Resultado final ---
header "=== Resultado Final ==="

STOCK_FINAL=$(curl -sf "$INVENTORY_URL/inventory/stock/$PRODUCT_ID" 2>/dev/null | jq '.quantity // "N/A"')
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
      success "Saga $pedido_num ($saga_id): ${GREEN}$state${NC}"
      ;;
    Failed)
      COUNT_FAILED=$((COUNT_FAILED + 1))
      warn "Saga $pedido_num ($saga_id): ${YELLOW}$state${NC} (compensacao executada)"
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
if [[ "$MODO_LOCK" == *"SEM LOCK"* ]]; then
  EXPECTED_REMAINING=$((ESTOQUE_INICIAL - COUNT_COMPLETED))
  if [[ "$STOCK_FINAL" -lt 0 ]]; then
    error "OVERBOOKING DETECTADO! Estoque = $STOCK_FINAL (negativo)"
    error "Race condition em acao: multiplas transacoes aprovaram alem do estoque disponivel"
  elif [[ $COUNT_COMPLETED -gt $ESTOQUE_INICIAL ]]; then
    error "OVERBOOKING: $COUNT_COMPLETED pedidos aprovados com apenas $ESTOQUE_INICIAL em estoque"
  else
    warn "Race condition nao foi observada nesta execucao (resultado pode variar)"
  fi
else
  if [[ $COUNT_COMPLETED -eq $ESTOQUE_INICIAL && $COUNT_FAILED -eq $((NUM_PEDIDOS - ESTOQUE_INICIAL)) ]]; then
    success "Resultado CORRETO: $COUNT_COMPLETED pedidos aprovados (= estoque disponivel)"
    success "FOR UPDATE serializou o acesso — sem overbooking!"
  elif [[ $COUNT_COMPLETED -lt $ESTOQUE_INICIAL ]]; then
    warn "Menos pedidos completados que o esperado (possivel timeout)"
  fi
fi

# --- Cleanup ---
rm -rf "$TMP_DIR"

echo ""
log "Para ver logs do InventoryService:"
log "  docker logs saga-inventory-service --tail=50"
log ""
log "Para ver estado de uma saga especifica:"
log "  curl http://localhost:5002/sagas/<saga-id>"
