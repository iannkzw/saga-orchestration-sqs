using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using OrderService.Data;
using OrderService.Models;
using Shared.Contracts.Commands;
using Shared.Contracts.Events;

namespace OrderService.Api;

public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/orders", CreateOrderAsync);
        app.MapGet("/orders/{id:guid}", GetOrderAsync);
        return app;
    }

    private static async Task<IResult> CreateOrderAsync(
        HttpContext context,
        OrderDbContext db,
        IPublishEndpoint publishEndpoint)
    {
        var requestBody = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body);

        var totalAmount = requestBody.GetProperty("totalAmount").GetDecimal();
        var itemsElement = requestBody.GetProperty("items");
        var itemsJson = itemsElement.GetRawText();

        var order = new Order
        {
            Id = Guid.NewGuid(),
            TotalAmount = totalAmount,
            ItemsJson = itemsJson,
            Status = OrderStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var customerId = requestBody.TryGetProperty("customerId", out var cid)
            ? cid.GetString() ?? string.Empty
            : string.Empty;

        var items = JsonSerializer.Deserialize<List<JsonElement>>(itemsJson) ?? [];
        var orderItems = items.Select(i => new OrderItem(
            i.GetProperty("productId").GetString() ?? string.Empty,
            i.GetProperty("quantity").GetInt32(),
            i.TryGetProperty("unitPrice", out var up) ? up.GetDecimal() : 0m
        )).ToList();

        var correlationId = Guid.NewGuid();
        await publishEndpoint.Publish(new OrderPlaced(
            correlationId,
            customerId,
            totalAmount,
            orderItems,
            DateTime.UtcNow
        ));

        order.SagaId = correlationId;
        order.Status = OrderStatus.Processing;
        order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Results.Created($"/orders/{order.Id}", new
        {
            orderId = order.Id,
            sagaId = correlationId,
            status = order.Status.ToString()
        });
    }

    private static async Task<IResult> GetOrderAsync(Guid id, OrderDbContext db)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id);
        if (order is null)
            return Results.NotFound();

        var items = string.IsNullOrEmpty(order.ItemsJson)
            ? (object)Array.Empty<object>()
            : JsonSerializer.Deserialize<JsonElement>(order.ItemsJson);

        return Results.Ok(new
        {
            orderId = order.Id,
            sagaId = order.SagaId,
            status = order.Status.ToString(),
            totalAmount = order.TotalAmount,
            items
        });
    }
}
