using MassTransit;
using Microsoft.EntityFrameworkCore;
using OrderService.Data;
using OrderService.Models;
using Shared.Contracts.Commands;
using Shared.Contracts.Events;
using Shared.Contracts.Replies;

namespace OrderService.StateMachine;

public class OrderStateMachine : MassTransitStateMachine<OrderSagaInstance>
{
    public State PaymentProcessing { get; private set; } = null!;
    public State InventoryReserving { get; private set; } = null!;
    public State ShippingScheduling { get; private set; } = null!;
    public State Compensating { get; private set; } = null!;

    public Event<OrderPlaced> OrderPlaced { get; private set; } = null!;
    public Event<PaymentReply> PaymentReply { get; private set; } = null!;
    public Event<InventoryReply> InventoryReply { get; private set; } = null!;
    public Event<ShippingReply> ShippingReply { get; private set; } = null!;
    public Event<ReleaseInventoryReply> ReleaseInventoryReply { get; private set; } = null!;
    public Event<RefundPaymentReply> RefundPaymentReply { get; private set; } = null!;

    public OrderStateMachine()
    {
        InstanceState(x => x.CurrentState);

        Event(() => OrderPlaced, e => e.CorrelateById(ctx => ctx.Message.CorrelationId));
        Event(() => PaymentReply, e => e.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => InventoryReply, e => e.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => ShippingReply, e => e.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => ReleaseInventoryReply, e => e.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => RefundPaymentReply, e => e.CorrelateById(ctx => ctx.Message.SagaId));

        Initially(
            When(OrderPlaced)
                .Then(ctx =>
                {
                    ctx.Saga.OrderId = ctx.Saga.CorrelationId;
                    ctx.Saga.TotalAmount = ctx.Message.TotalAmount;
                    ctx.Saga.CreatedAt = DateTime.UtcNow;
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .TransitionTo(PaymentProcessing)
                .PublishAsync(ctx => ctx.Init<ProcessPayment>(new ProcessPayment
                {
                    CorrelationId = ctx.Saga.CorrelationId,
                    OrderId = ctx.Saga.OrderId,
                    Amount = ctx.Message.TotalAmount,
                    CustomerId = ctx.Message.CustomerId
                }))
        );

        During(PaymentProcessing,
            When(PaymentReply, ctx => ctx.Message.Success)
                .Then(ctx =>
                {
                    ctx.Saga.PaymentId = ctx.Message.TransactionId;
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .TransitionTo(InventoryReserving)
                .PublishAsync(ctx => ctx.Init<ReserveInventory>(new ReserveInventory
                {
                    CorrelationId = ctx.Saga.CorrelationId,
                    OrderId = ctx.Saga.OrderId
                })),

            When(PaymentReply, ctx => !ctx.Message.Success)
                .ThenAsync(async ctx =>
                {
                    ctx.Saga.FailureReason = ctx.Message.ErrorMessage ?? "Payment failed";
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                    await UpdateOrderStatus(ctx.GetServiceOrCreateInstance<OrderDbContext>(), ctx.Saga);
                })
                .TransitionTo(Final)
        );

        During(InventoryReserving,
            When(InventoryReply, ctx => ctx.Message.Success)
                .Then(ctx =>
                {
                    ctx.Saga.ReservationId = ctx.Message.ReservationId;
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .TransitionTo(ShippingScheduling)
                .PublishAsync(ctx => ctx.Init<ScheduleShipping>(new ScheduleShipping
                {
                    CorrelationId = ctx.Saga.CorrelationId,
                    OrderId = ctx.Saga.OrderId
                })),

            When(InventoryReply, ctx => !ctx.Message.Success)
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = ctx.Message.ErrorMessage ?? "Inventory reservation failed";
                    ctx.Saga.CompensationStep = "CancelPayment";
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .TransitionTo(Compensating)
                .PublishAsync(ctx => ctx.Init<RefundPayment>(new RefundPayment
                {
                    SagaId = ctx.Saga.CorrelationId,
                    OrderId = ctx.Saga.OrderId,
                    Amount = ctx.Saga.TotalAmount,
                    TransactionId = ctx.Saga.PaymentId ?? string.Empty,
                    IdempotencyKey = $"refund-payment-{ctx.Saga.OrderId}"
                }))
        );

        During(ShippingScheduling,
            When(ShippingReply, ctx => ctx.Message.Success)
                .ThenAsync(async ctx =>
                {
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                    await UpdateOrderStatus(ctx.GetServiceOrCreateInstance<OrderDbContext>(), ctx.Saga);
                })
                .TransitionTo(Final),

            When(ShippingReply, ctx => !ctx.Message.Success)
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = ctx.Message.ErrorMessage ?? "Shipping scheduling failed";
                    ctx.Saga.CompensationStep = "CancelInventory";
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .TransitionTo(Compensating)
                .PublishAsync(ctx => ctx.Init<ReleaseInventory>(new ReleaseInventory
                {
                    SagaId = ctx.Saga.CorrelationId,
                    OrderId = ctx.Saga.OrderId,
                    ReservationId = ctx.Saga.ReservationId ?? string.Empty,
                    IdempotencyKey = $"release-inventory-{ctx.Saga.OrderId}"
                }))
        );

        During(Compensating,
            When(ReleaseInventoryReply)
                .Then(ctx =>
                {
                    ctx.Saga.CompensationStep = "CancelPayment";
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .PublishAsync(ctx => ctx.Init<RefundPayment>(new RefundPayment
                {
                    SagaId = ctx.Saga.CorrelationId,
                    OrderId = ctx.Saga.OrderId,
                    Amount = ctx.Saga.TotalAmount,
                    TransactionId = ctx.Saga.PaymentId ?? string.Empty,
                    IdempotencyKey = $"refund-payment-{ctx.Saga.OrderId}"
                })),

            When(RefundPaymentReply)
                .ThenAsync(async ctx =>
                {
                    ctx.Saga.CompensationStep = null;
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                    await UpdateOrderStatus(ctx.GetServiceOrCreateInstance<OrderDbContext>(), ctx.Saga);
                })
                .TransitionTo(Final)
        );
    }

    private static async Task UpdateOrderStatus(OrderDbContext db, OrderSagaInstance saga)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == saga.OrderId);
        if (order is null) return;

        order.Status = saga.FailureReason is null ? OrderStatus.Completed : OrderStatus.Failed;
        order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
}
