#!/usr/bin/env bash
# =============================================================================
# happy-path-demo.sh
# Demonstracao do fluxo da saga em 4 cenarios sequenciais.
#
# Cenarios:
#   1. Happy path completo             → saga Completed com todas as transicoes
#   2. Falha no pagamento              → saga Failed, sem InventoryReserving
#   3. Falha no inventario             → saga Failed com PaymentRefunding
#   4. Falha no shipping               → saga Failed com cascata completa
#
# Uso:
#   bash scripts/happy-path-demo.sh
# =============================================================================

set -euo pipefail

source "$(dirname "$0")/lib/common.sh"

ESTOQUE_DEMO=5  # Estoque inicial para os 4 cenarios — nenhum deve esgotar

# Contadores de resultado
CENARIOS_OK=0
CENARIOS_FAIL=0

# cenario_ok MSG / cenario_fail MSG — registra resultado e imprime
cenario_ok()   { success "$*"; CENARIOS_OK=$((CENARIOS_OK + 1)); }
cenario_fail() { error "$*"; CENARIOS_FAIL=$((CENARIOS_FAIL + 1)); }

# assert_state SAGA_ID EXPECTED_STATE ELAPSED
assert_state() {
  local saga_id="$1" expected="$2" elapsed="$3" actual
  actual=$(curl -sf "$ORCHESTRATOR_URL/sagas/$saga_id" 2>/dev/null | jq -r '.state // "Unknown"')
  if [[ "$actual" == "$expected" ]]; then
    cenario_ok "Saga atingiu $expected em ${elapsed}s"
  else
    cenario_fail "Esperado $expected, obtido $actual"
  fi
}

# assert_transition_present SAGA_ID STATE_TO
assert_transition_present() {
  local saga_id="$1" state="$2" transitions
  transitions=$(get_transitions "$saga_id")
  if echo "$transitions" | grep -q "$state"; then
    cenario_ok "$state presente nas transicoes"
  else
    cenario_fail "$state AUSENTE nas transicoes (era esperado)"
  fi
}

# assert_transition_absent SAGA_ID STATE_TO
assert_transition_absent() {
  local saga_id="$1" state="$2" transitions
  transitions=$(get_transitions "$saga_id")
  if echo "$transitions" | grep -q "$state"; then
    cenario_fail "$state presente nas transicoes (nao era esperado)"
  else
    cenario_ok "$state ausente nas transicoes (correto)"
  fi
}

# assert_order_status ORDER_ID EXPECTED_STATUS
assert_order_status() {
  local order_id="$1" expected="$2" actual
  local deadline=$((SECONDS + 15))
  while [[ $SECONDS -lt $deadline ]]; do
    actual=$(curl -sf "$ORDER_URL/orders/$order_id" 2>/dev/null | jq -r '.status // "Unknown"')
    [[ "$actual" == "$expected" ]] && break
    sleep 1
  done
  if [[ "$actual" == "$expected" ]]; then
    cenario_ok "order.status = $expected (pedido atualizado pelo Worker)"
  else
    cenario_fail "order.status esperado $expected, obtido $actual"
  fi
}

# --- Verificar dependencias ---
for cmd in curl jq; do
  if ! command -v "$cmd" &>/dev/null; then
    error "Dependencia ausente: $cmd"
    exit 1
  fi
done

# --- Verificar saude dos servicos ---
header "=== Verificando servicos ==="
check_health "OrderService"     "$ORDER_URL"
check_health "SagaOrchestrator" "$ORCHESTRATOR_URL"
check_health "InventoryService" "$INVENTORY_URL"

# --- Resetar estoque para os cenarios ---
reset_stock "$PRODUCT_ID" "$ESTOQUE_DEMO"
STOCK_INICIAL=$(get_stock "$PRODUCT_ID")
log "Estoque inicial de $PRODUCT_ID: $STOCK_INICIAL unidades"

# =============================================================================
# CENARIO 1: Happy Path Completo
# =============================================================================
header "=== Cenario 1: Happy Path ==="

RESPONSE=$(curl -sf -X POST "$ORDER_URL/orders" \
  -H "Content-Type: application/json" \
  -d "{
    \"totalAmount\": 99.90,
    \"items\": [{
      \"productId\": \"$PRODUCT_ID\",
      \"quantity\": 1,
      \"unitPrice\": 99.90
    }]
  }" 2>/dev/null)

ORDER_ID=$(echo "$RESPONSE" | jq -r '.orderId // empty')
SAGA_ID=$(echo  "$RESPONSE" | jq -r '.sagaId  // empty')

if [[ -z "$ORDER_ID" ]]; then
  cenario_fail "Falha ao criar pedido (resposta invalida)"
else
  success "Pedido criado: orderId=$ORDER_ID sagaId=$SAGA_ID"

  START=$SECONDS
  FINAL_STATE=$(poll_saga "$SAGA_ID" 60)
  ELAPSED=$((SECONDS - START))

  assert_state "$SAGA_ID" "Completed" "$ELAPSED"
  assert_order_status "$ORDER_ID" "Completed"

  TRANSITIONS=$(get_transitions "$SAGA_ID")
  EXPECTED_SEQ="PaymentProcessing InventoryReserving ShippingScheduling Completed"
  if [[ "$TRANSITIONS" == "$EXPECTED_SEQ" ]]; then
    cenario_ok "Transicoes: Pending → $TRANSITIONS"
  else
    cenario_fail "Sequencia inesperada: '$TRANSITIONS' (esperado: '$EXPECTED_SEQ')"
  fi

  STOCK_APOS=$(get_stock "$PRODUCT_ID")
  ESPERADO=$((STOCK_INICIAL - 1))
  if [[ "$STOCK_APOS" -eq $ESPERADO ]]; then
    cenario_ok "Estoque de $PRODUCT_ID: $STOCK_APOS (era $STOCK_INICIAL)"
  else
    cenario_fail "Estoque esperado $ESPERADO, obtido $STOCK_APOS"
  fi
fi

# =============================================================================
# CENARIO 2: Falha no Pagamento
# =============================================================================
header "=== Cenario 2: Falha no Pagamento ==="

STOCK_ANTES_C2=$(get_stock "$PRODUCT_ID")

RESPONSE=$(curl -sf -X POST "$ORDER_URL/orders" \
  -H "Content-Type: application/json" \
  -H "X-Simulate-Failure: payment" \
  -d "{
    \"totalAmount\": 99.90,
    \"items\": [{
      \"productId\": \"$PRODUCT_ID\",
      \"quantity\": 1,
      \"unitPrice\": 99.90
    }]
  }" 2>/dev/null)

ORDER_ID=$(echo "$RESPONSE" | jq -r '.orderId // empty')
SAGA_ID=$(echo  "$RESPONSE" | jq -r '.sagaId  // empty')

if [[ -z "$ORDER_ID" ]]; then
  cenario_fail "Falha ao criar pedido (resposta invalida)"
else
  success "Pedido criado: orderId=$ORDER_ID sagaId=$SAGA_ID"

  START=$SECONDS
  FINAL_STATE=$(poll_saga "$SAGA_ID" 60)
  ELAPSED=$((SECONDS - START))

  assert_state               "$SAGA_ID" "Failed" "$ELAPSED"
  assert_order_status        "$ORDER_ID" "Failed"
  assert_transition_present  "$SAGA_ID" "PaymentProcessing"
  assert_transition_absent   "$SAGA_ID" "InventoryReserving"

  STOCK_APOS=$(get_stock "$PRODUCT_ID")
  if [[ "$STOCK_APOS" -eq "$STOCK_ANTES_C2" ]]; then
    cenario_ok "Estoque nao alterado: $STOCK_APOS (pagamento falhou antes de reservar)"
  else
    cenario_fail "Estoque alterado inesperadamente: era $STOCK_ANTES_C2, agora $STOCK_APOS"
  fi
fi

# =============================================================================
# CENARIO 3: Falha no Inventario
# =============================================================================
header "=== Cenario 3: Falha no Inventario ==="

STOCK_ANTES_C3=$(get_stock "$PRODUCT_ID")

RESPONSE=$(curl -sf -X POST "$ORDER_URL/orders" \
  -H "Content-Type: application/json" \
  -H "X-Simulate-Failure: inventory" \
  -d "{
    \"totalAmount\": 99.90,
    \"items\": [{
      \"productId\": \"$PRODUCT_ID\",
      \"quantity\": 1,
      \"unitPrice\": 99.90
    }]
  }" 2>/dev/null)

ORDER_ID=$(echo "$RESPONSE" | jq -r '.orderId // empty')
SAGA_ID=$(echo  "$RESPONSE" | jq -r '.sagaId  // empty')

if [[ -z "$ORDER_ID" ]]; then
  cenario_fail "Falha ao criar pedido (resposta invalida)"
else
  success "Pedido criado: orderId=$ORDER_ID sagaId=$SAGA_ID"

  START=$SECONDS
  FINAL_STATE=$(poll_saga "$SAGA_ID" 60)
  ELAPSED=$((SECONDS - START))

  assert_state              "$SAGA_ID" "Failed" "$ELAPSED"
  assert_order_status       "$ORDER_ID" "Failed"
  assert_transition_present "$SAGA_ID" "PaymentRefunding"
  assert_transition_absent  "$SAGA_ID" "ShippingScheduling"

  STOCK_APOS=$(get_stock "$PRODUCT_ID")
  if [[ "$STOCK_APOS" -eq "$STOCK_ANTES_C3" ]]; then
    cenario_ok "Estoque nao alterado: $STOCK_APOS (inventario falhou antes de reservar)"
  else
    cenario_fail "Estoque alterado inesperadamente: era $STOCK_ANTES_C3, agora $STOCK_APOS"
  fi
fi

# =============================================================================
# CENARIO 4: Falha no Shipping
# =============================================================================
header "=== Cenario 4: Falha no Shipping ==="

RESPONSE=$(curl -sf -X POST "$ORDER_URL/orders" \
  -H "Content-Type: application/json" \
  -H "X-Simulate-Failure: shipping" \
  -d "{
    \"totalAmount\": 99.90,
    \"items\": [{
      \"productId\": \"$PRODUCT_ID\",
      \"quantity\": 1,
      \"unitPrice\": 99.90
    }]
  }" 2>/dev/null)

ORDER_ID=$(echo "$RESPONSE" | jq -r '.orderId // empty')
SAGA_ID=$(echo  "$RESPONSE" | jq -r '.sagaId  // empty')

if [[ -z "$ORDER_ID" ]]; then
  cenario_fail "Falha ao criar pedido (resposta invalida)"
else
  success "Pedido criado: orderId=$ORDER_ID sagaId=$SAGA_ID"

  START=$SECONDS
  FINAL_STATE=$(poll_saga "$SAGA_ID" 60)
  ELAPSED=$((SECONDS - START))

  assert_state              "$SAGA_ID" "Failed" "$ELAPSED"
  assert_order_status       "$ORDER_ID" "Failed"
  assert_transition_present "$SAGA_ID" "InventoryReleasing"
  assert_transition_present "$SAGA_ID" "PaymentRefunding"
fi

# =============================================================================
# RESUMO FINAL
# =============================================================================
TOTAL=$((CENARIOS_OK + CENARIOS_FAIL))
echo ""
echo -e "${BOLD}--- Resumo Final ---${NC}"
echo -e "Verificacoes OK:    ${GREEN}${BOLD}$CENARIOS_OK${NC} / $TOTAL"
if [[ $CENARIOS_FAIL -gt 0 ]]; then
  echo -e "Verificacoes FAIL:  ${RED}${BOLD}$CENARIOS_FAIL${NC} / $TOTAL"
  echo ""
  error "Alguns cenarios nao passaram. Verifique os logs acima."
  exit 1
else
  echo ""
  success "Todos os $TOTAL cenarios passaram!"
fi
