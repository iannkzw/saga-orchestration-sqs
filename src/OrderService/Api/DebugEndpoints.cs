using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using OrderService.Data;
using OrderService.Diagnostics;
using OrderService.Models;
using Shared.Contracts.Events;

namespace OrderService.Api;

// Endpoints de observabilidade / injecao de falha usados apenas por testes de integracao.
// Habilitados somente quando ENABLE_DEBUG_ENDPOINTS=true (default: false).
public static class DebugEndpoints
{
    public static IEndpointRouteBuilder MapDebugEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/debug/metrics/optimistic-retries",
            (OptimisticRetryCounter counter) => Results.Ok(new { count = counter.Value }));

        app.MapPost("/debug/metrics/optimistic-retries/reset",
            (OptimisticRetryCounter counter) =>
            {
                counter.Reset();
                return Results.NoContent();
            });

        app.MapPost("/debug/republish-order-placed/{sagaId:guid}",
            async (Guid sagaId, int count, OrderDbContext db, IPublishEndpoint publish) =>
            {
                if (count <= 0) return Results.BadRequest(new { error = "count must be > 0" });

                var saga = await db.SagaInstances.FirstOrDefaultAsync(s => s.CorrelationId == sagaId);
                if (saga is null) return Results.NotFound();

                var items = JsonSerializer.Deserialize<List<OrderItem>>(saga.ItemsJson) ?? [];

                var tasks = Enumerable.Range(0, count).Select(_ => publish.Publish(new OrderPlaced(
                    CorrelationId: saga.CorrelationId,
                    OrderId: saga.OrderId,
                    CustomerId: saga.CustomerId,
                    TotalAmount: saga.TotalAmount,
                    Items: items,
                    PlacedAt: DateTime.UtcNow,
                    SimulateFailure: null
                )));

                await Task.WhenAll(tasks);
                return Results.Accepted(value: new { republished = count, sagaId });
            });

        return app;
    }
}
