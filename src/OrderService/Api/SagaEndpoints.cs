using Microsoft.EntityFrameworkCore;
using OrderService.Data;

namespace OrderService.Api;

public static class SagaEndpoints
{
    public static IEndpointRouteBuilder MapSagaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/sagas/{id:guid}", GetSagaAsync);
        return app;
    }

    private static async Task<IResult> GetSagaAsync(Guid id, OrderDbContext db)
    {
        var saga = await db.SagaInstances.FirstOrDefaultAsync(s => s.CorrelationId == id);

        if (saga is null)
            return Results.NotFound();

        return Results.Ok(new
        {
            sagaId = saga.CorrelationId,
            orderId = saga.OrderId,
            state = saga.CurrentState,
            createdAt = saga.CreatedAt,
            updatedAt = saga.UpdatedAt,
            paymentId = saga.PaymentId,
            reservationId = saga.ReservationId,
            failureReason = saga.FailureReason,
            compensationStep = saga.CompensationStep
        });
    }
}
