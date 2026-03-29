using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderService.Data;
using OrderService.Models;
using Shared.Contracts.Commands;
using Shared.Extensions;
using Shared.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

var sqsServiceUrl = builder.Configuration["AWS_SERVICE_URL"] ?? "http://localhost:4566";
builder.Services.AddSagaConnectivity(sqsServiceUrl);

var connectionString = builder.Configuration.GetConnectionString("SagaDb")
    ?? "Host=localhost;Port=5432;Database=saga_db;Username=saga;Password=saga_pass";
builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseNpgsql(connectionString));

var sagaOrchestratorUrl = builder.Configuration["SAGA_ORCHESTRATOR_URL"] ?? "http://saga-orchestrator:5002";
builder.Services.AddHttpClient("SagaOrchestrator", client =>
{
    client.BaseAddress = new Uri(sagaOrchestratorUrl);
});

var app = builder.Build();

// EnsureCreated no startup (PoC — sem migrations formais)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    db.Database.EnsureCreated();
}

app.MapGet("/health", (StartupConnectivityCheck checks) => Results.Ok(new
{
    status = "healthy",
    service = "OrderService",
    connections = new
    {
        sqs = checks.SqsHealthy,
        postgres = checks.PostgresHealthy
    }
}));

app.MapPost("/orders", async (HttpContext context, OrderDbContext db, IHttpClientFactory httpClientFactory) =>
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

    // Chamar SagaOrchestrator para iniciar a saga
    var items = JsonSerializer.Deserialize<List<JsonElement>>(itemsJson) ?? [];
    var orderItems = items.Select(i => new OrderItem
    {
        ProductId = i.GetProperty("productId").GetString() ?? string.Empty,
        Quantity = i.GetProperty("quantity").GetInt32(),
        UnitPrice = i.TryGetProperty("unitPrice", out var up) ? up.GetDecimal() : 0m
    }).ToList();

    var sagaId = Guid.NewGuid();
    var createOrderCommand = new CreateOrder
    {
        SagaId = sagaId,
        OrderId = order.Id,
        TotalAmount = totalAmount,
        Items = orderItems,
        IdempotencyKey = $"{sagaId}-create",
        Timestamp = DateTime.UtcNow
    };

    var client = httpClientFactory.CreateClient("SagaOrchestrator");

    // Propagar header de simulacao de falha para o orquestrador
    var simulateFailure = context.Request.Headers["X-Simulate-Failure"].FirstOrDefault();
    if (!string.IsNullOrEmpty(simulateFailure))
    {
        client.DefaultRequestHeaders.Add("X-Simulate-Failure", simulateFailure);
    }

    var sagaResponse = await client.PostAsJsonAsync("/sagas", createOrderCommand);
    sagaResponse.EnsureSuccessStatusCode();

    var sagaResult = await sagaResponse.Content.ReadFromJsonAsync<JsonElement>();
    var returnedSagaId = sagaResult.GetProperty("sagaId").GetGuid();

    order.SagaId = returnedSagaId;
    order.Status = OrderStatus.Processing;
    order.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();

    return Results.Created($"/orders/{order.Id}", new
    {
        orderId = order.Id,
        sagaId = returnedSagaId,
        status = order.Status.ToString()
    });
});

app.MapGet("/orders/{id:guid}", async (Guid id, OrderDbContext db, IHttpClientFactory httpClientFactory) =>
{
    var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id);
    if (order is null)
        return Results.NotFound();

    object? sagaData = null;

    if (order.SagaId.HasValue)
    {
        try
        {
            var client = httpClientFactory.CreateClient("SagaOrchestrator");
            var sagaResponse = await client.GetAsync($"/sagas/{order.SagaId.Value}");
            if (sagaResponse.IsSuccessStatusCode)
            {
                var sagaResult = await sagaResponse.Content.ReadFromJsonAsync<JsonElement>();
                sagaData = new
                {
                    state = sagaResult.GetProperty("state").GetString(),
                    transitions = sagaResult.GetProperty("transitions").EnumerateArray().Select(t => new
                    {
                        from = t.GetProperty("from").GetString(),
                        to = t.GetProperty("to").GetString(),
                        triggeredBy = t.GetProperty("triggeredBy").GetString(),
                        timestamp = t.GetProperty("timestamp").GetString()
                    }).ToList()
                };
            }
        }
        catch
        {
            // Se falhar ao buscar saga, retorna order com saga: null
        }
    }

    var items = string.IsNullOrEmpty(order.ItemsJson)
        ? (object)Array.Empty<object>()
        : JsonSerializer.Deserialize<JsonElement>(order.ItemsJson);

    return Results.Ok(new
    {
        orderId = order.Id,
        sagaId = order.SagaId,
        status = order.Status.ToString(),
        totalAmount = order.TotalAmount,
        items,
        saga = sagaData
    });
});

app.Run();
