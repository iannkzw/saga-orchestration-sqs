# Tasks: Command-Reply Flow

## T1: Implementar PaymentService Worker
- **Arquivo:** `src/PaymentService/Worker.cs`
- **O que:** Substituir placeholder por worker real que faz polling de `payment-commands`, deserializa `ProcessPayment`, simula processamento, envia `PaymentReply(Success=true, TransactionId)` para `payment-replies`, deleta mensagem
- **Criterio:** Worker processa comando e envia reply visivel nos logs
- **Status:** DONE

## T2: Implementar InventoryService Worker
- **Arquivo:** `src/InventoryService/Worker.cs`
- **O que:** Substituir placeholder por worker real que faz polling de `inventory-commands`, deserializa `ReserveInventory`, simula reserva, envia `InventoryReply(Success=true, ReservationId)` para `inventory-replies`, deleta mensagem
- **Criterio:** Worker processa comando e envia reply visivel nos logs
- **Status:** DONE

## T3: Implementar ShippingService Worker
- **Arquivo:** `src/ShippingService/Worker.cs`
- **O que:** Substituir placeholder por worker real que faz polling de `shipping-commands`, deserializa `ScheduleShipping`, simula agendamento, envia `ShippingReply(Success=true, TrackingNumber)` para `shipping-replies`, deleta mensagem
- **Criterio:** Worker processa comando e envia reply visivel nos logs
- **Status:** DONE

## T4: Atualizar arquivos de estado do projeto
- **Arquivos:** `.specs/project/STATE.md`, `.specs/project/ROADMAP.md`, tasks.md
- **O que:** Marcar feature como DONE, registrar decisao no STATE.md
- **Status:** DONE
