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

# assert_saga_terminal SAGA_ID ELAPSED
# Valida que a saga atingiu o estado "Final" (estado terminal do MassTransit).
assert_saga_terminal() {
  local saga_id="$1" elapsed="$2" actual
  actual=$(curl -sf "$ORDER_URL/sagas/$saga_id" 2>/dev/null | jq -r '.state // "Unknown"')
  if [[ "$actual" == "Final" ]]; then
    cenario_ok "Saga atingiu estado Final em ${elapsed}s"
  else
    cenario_fail "Esperado Final, obtido $actual"
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

  assert_saga_terminal "$SAGA_ID" "$ELAPSED"
  assert_order_status "$ORDER_ID" "Completed"

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

  assert_saga_terminal  "$SAGA_ID" "$ELAPSED"
  assert_order_status   "$ORDER_ID" "Failed"

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

  assert_saga_terminal  "$SAGA_ID" "$ELAPSED"
  assert_order_status   "$ORDER_ID" "Failed"

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

  assert_saga_terminal  "$SAGA_ID" "$ELAPSED"
  assert_order_status   "$ORDER_ID" "Failed"
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
