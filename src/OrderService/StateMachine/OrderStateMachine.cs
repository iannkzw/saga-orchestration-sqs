using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using OrderService.Data;
using OrderService.Models;
using Shared.Contracts.Commands;
using Shared.Contracts.Events;

namespace OrderService.StateMachine;

public class OrderStateMachine : MassTransitStateMachine<OrderSagaInstance>
{
    public State PaymentProcessing { get; private set; } = null!;
    public State InventoryReserving { get; private set; } = null!;
    public State ShippingScheduling { get; private set; } = null!;
    public State Compensating { get; private set; } = null!;

    public Event<OrderPlaced> OrderPlaced { get; private set; } = null!;
    public Event<PaymentCompleted> PaymentCompleted { get; private set; } = null!;
    public Event<PaymentFailed> PaymentFailed { get; private set; } = null!;
    public Event<InventoryReserved> InventoryReserved { get; private set; } = null!;
    public Event<InventoryFailed> InventoryFailed { get; private set; } = null!;
    public Event<ShippingScheduled> ShippingScheduled { get; private set; } = null!;
    public Event<ShippingFailed> ShippingFailed { get; private set; } = null!;
    public Event<InventoryCancelled> InventoryCancelled { get; private set; } = null!;
    public Event<PaymentCancelled> PaymentCancelled { get; private set; } = null!;

    public OrderStateMachine()
    {
        InstanceState(x => x.CurrentState);

        Event(() => OrderPlaced, e => e.CorrelateById(ctx => ctx.Message.CorrelationId));
        Event(() => PaymentCompleted, e => e.CorrelateById(ctx => ctx.Message.CorrelationId));
        Event(() => PaymentFailed, e => e.CorrelateById(ctx => ctx.Message.CorrelationId));
        Event(() => InventoryReserved, e => e.CorrelateById(ctx => ctx.Message.CorrelationId));
        Event(() => InventoryFailed, e => e.CorrelateById(ctx => ctx.Message.CorrelationId));
        Event(() => ShippingScheduled, e => e.CorrelateById(ctx => ctx.Message.CorrelationId));
        Event(() => ShippingFailed, e => e.CorrelateById(ctx => ctx.Message.CorrelationId));
        Event(() => InventoryCancelled, e => e.CorrelateById(ctx => ctx.Message.CorrelationId));
        Event(() => PaymentCancelled, e => e.CorrelateById(ctx => ctx.Message.CorrelationId));

        Initially(
            When(OrderPlaced)
                .Then(ctx =>
                {
                    ctx.Saga.OrderId = ctx.Message.OrderId;
                    ctx.Saga.TotalAmount = ctx.Message.TotalAmount;
                    ctx.Saga.ItemsJson = JsonSerializer.Serialize(ctx.Message.Items);
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
            When(PaymentCompleted)
                .Then(ctx =>
                {
                    ctx.Saga.PaymentId = ctx.Message.PaymentId;
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .TransitionTo(InventoryReserving)
                .PublishAsync(ctx => ctx.Init<ReserveInventory>(new ReserveInventory
                {
                    CorrelationId = ctx.Saga.CorrelationId,
                    OrderId = ctx.Saga.OrderId,
                    Items = JsonSerializer.Deserialize<List<OrderItem>>(ctx.Saga.ItemsJson) ?? []
                })),

            When(PaymentFailed)
                .ThenAsync(async ctx =>
                {
                    ctx.Saga.FailureReason = ctx.Message.Reason;
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                    await UpdateOrderStatus(ctx.GetServiceOrCreateInstance<OrderDbContext>(), ctx.Saga);
                })
                .TransitionTo(Final)
        );

        During(InventoryReserving,
            When(InventoryReserved)
                .Then(ctx =>
                {
                    ctx.Saga.ReservationId = ctx.Message.ReservationId;
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .TransitionTo(ShippingScheduling)
                .PublishAsync(ctx => ctx.Init<ScheduleShipping>(new ScheduleShipping
                {
                    CorrelationId = ctx.Saga.CorrelationId,
                    OrderId = ctx.Saga.OrderId,
                    Items = JsonSerializer.Deserialize<List<OrderItem>>(ctx.Saga.ItemsJson) ?? []
                })),

            When(InventoryFailed)
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = ctx.Message.Reason;
                    ctx.Saga.CompensationStep = "CancelPayment";
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .TransitionTo(Compensating)
                .PublishAsync(ctx => ctx.Init<CancelPayment>(new CancelPayment
                {
                    CorrelationId = ctx.Saga.CorrelationId,
                    PaymentId = ctx.Saga.PaymentId ?? string.Empty
                }))
        );

        During(ShippingScheduling,
            When(ShippingScheduled)
                .ThenAsync(async ctx =>
                {
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                    await UpdateOrderStatus(ctx.GetServiceOrCreateInstance<OrderDbContext>(), ctx.Saga);
                })
                .TransitionTo(Final),

            When(ShippingFailed)
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = ctx.Message.Reason;
                    ctx.Saga.CompensationStep = "CancelInventory";
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .TransitionTo(Compensating)
                .PublishAsync(ctx => ctx.Init<CancelInventory>(new CancelInventory
                {
                    CorrelationId = ctx.Saga.CorrelationId,
                    ReservationId = ctx.Saga.ReservationId ?? string.Empty
                }))
        );

        During(Compensating,
            When(InventoryCancelled)
                .Then(ctx =>
                {
                    ctx.Saga.CompensationStep = "CancelPayment";
                    ctx.Saga.UpdatedAt = DateTime.UtcNow;
                })
                .PublishAsync(ctx => ctx.Init<CancelPayment>(new CancelPayment
                {
                    CorrelationId = ctx.Saga.CorrelationId,
                    PaymentId = ctx.Saga.PaymentId ?? string.Empty
                })),

            When(PaymentCancelled)
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
        var order = await db.Orders.FirstOrDefaultAsync(o => o.SagaId == saga.CorrelationId);
        if (order is null) return;

        order.Status = saga.FailureReason is null ? OrderStatus.Completed : OrderStatus.Failed;
        order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
}
