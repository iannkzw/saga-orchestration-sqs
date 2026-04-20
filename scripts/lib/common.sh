#!/usr/bin/env bash
# =============================================================================
# scripts/lib/common.sh
# Funcoes e variaveis compartilhadas pelos scripts de demo.
# Uso: source "$(dirname "$0")/lib/common.sh"
# =============================================================================

ORDER_URL="${ORDER_URL:-http://localhost:5001}"
INVENTORY_URL="${INVENTORY_URL:-http://localhost:5004}"
PRODUCT_ID="${PRODUCT_ID:-PROD-001}"

# --- Cores ---
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

# check_health NAME URL
# Verifica se o servico responde em /health. Aborta se nao.
check_health() {
  local name="$1"
  local url="$2"
  if curl -sf "$url/health" > /dev/null 2>&1; then
    success "$name: OK ($url)"
  else
    error "$name nao esta respondendo em $url"
    error "Execute: docker compose up -d e aguarde os servicos ficarem saudaveis"
    exit 1
  fi
}

# poll_saga SAGA_ID TIMEOUT
# Poll a cada 2s ate estado terminal (Final ou Failed) ou timeout.
# Com MassTransit o happy path termina em "Final", falhas em "Final" (FailureReason preenchido).
# O status do pedido em si e consultado via GET /orders/{id}.
# Imprime o estado final via echo — capturar com $().
poll_saga() {
  local saga_id="$1"
  local timeout="${2:-60}"
  local elapsed=0
  local state

  while [[ $elapsed -lt $timeout ]]; do
    state=$(curl -sf "$ORDER_URL/sagas/$saga_id" 2>/dev/null | jq -r '.state // "Unknown"')
    if [[ "$state" == "Final" ]]; then
      echo "$state"
      return 0
    fi
    sleep 2
    elapsed=$((elapsed + 2))
  done

  echo "Timeout"
}

# get_stock PRODUCT_ID
# Retorna a quantidade atual em estoque.
get_stock() {
  local product_id="$1"
  curl -sf "$INVENTORY_URL/inventory/stock/$product_id" 2>/dev/null \
    | jq -r '.quantity // "N/A"'
}

# reset_stock PRODUCT_ID QUANTITY
# Reseta o estoque do produto para a quantidade informada.
reset_stock() {
  local product_id="$1"
  local quantity="$2"
  curl -sf -X POST "$INVENTORY_URL/inventory/reset" \
    -H "Content-Type: application/json" \
    -d "{\"productId\": \"$product_id\", \"quantity\": $quantity}" > /dev/null
}
