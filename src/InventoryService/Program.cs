using System.Text.Json;
using InventoryService;
using Shared.Extensions;
using Shared.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHostedService<InventoryService.Worker>();
var sqsServiceUrl = builder.Configuration["AWS_SERVICE_URL"] ?? "http://localhost:4566";
builder.Services.AddSagaConnectivity(sqsServiceUrl);
builder.Services.AddSagaTracing("InventoryService");
builder.Services.AddSingleton<InventoryRepository>();

var app = builder.Build();

// Garantir tabelas de inventario no startup
var inventoryRepo = app.Services.GetRequiredService<InventoryRepository>();
await inventoryRepo.EnsureTablesAsync();

app.MapGet("/health", (StartupConnectivityCheck checks) => Results.Ok(new
{
    status = "healthy",
    service = "InventoryService",
    connections = new
    {
        sqs = checks.SqsHealthy,
        postgres = checks.PostgresHealthy
    }
}));

// Consulta o estoque atual de um produto
app.MapGet("/inventory/stock/{productId}", async (string productId, InventoryRepository repo) =>
{
    var stock = await repo.GetStockAsync(productId);
    if (stock is null)
        return Results.NotFound(new { error = $"Produto '{productId}' nao encontrado" });

    return Results.Ok(new { productId, quantity = stock });
});

// Reseta o estoque de um produto (util para demos e testes)
app.MapPost("/inventory/reset", async (HttpContext context, InventoryRepository repo) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body);
    var productId = body.TryGetProperty("productId", out var pid) ? pid.GetString() ?? "PROD-001" : "PROD-001";
    var quantity = body.TryGetProperty("quantity", out var qty) ? qty.GetInt32() : 2;

    await repo.ResetStockAsync(productId, quantity);
    return Results.Ok(new { productId, quantity, message = "Estoque resetado com sucesso" });
});

app.Run();
