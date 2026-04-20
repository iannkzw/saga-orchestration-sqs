using MassTransit;
using Microsoft.EntityFrameworkCore;
using OrderService.Api;
using OrderService.Data;
using OrderService.Models;
using OrderService.StateMachine;
using Shared.Extensions;
using Shared.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

var sqsServiceUrl = builder.Configuration["AWS_SERVICE_URL"] ?? "http://localhost:4566";
builder.Services.AddSagaConnectivity(sqsServiceUrl);
builder.Services.AddSagaTracing("order-service");
builder.Services.AddSagaLogging("order-service");

var connectionString = builder.Configuration.GetConnectionString("SagaDb")
    ?? "Host=localhost;Port=5432;Database=saga_db;Username=saga;Password=saga_pass";
builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddMassTransit(cfg =>
{
    cfg.AddSagaStateMachine<OrderStateMachine, OrderSagaInstance, OrderStateMachineDefinition>()
       .EntityFrameworkRepository(r =>
       {
           r.ConcurrencyMode = ConcurrencyMode.Pessimistic;
           r.UsePostgres();
           r.AddDbContext<DbContext, OrderDbContext>((_, options) =>
               options.UseNpgsql(connectionString));
       });

    cfg.AddEntityFrameworkOutbox<OrderDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
        o.QueryDelay = TimeSpan.FromSeconds(1);
        o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
    });

    cfg.UsingAmazonSqs((context, sqsCfg) =>
    {
        sqsCfg.ConfigureSqsHost(builder.Configuration);
        sqsCfg.UseMessageRetry(r => r.Intervals(500, 1000, 2000, 5000));
        sqsCfg.ConfigureEndpoints(context);
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    await db.Database.MigrateAsync();
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

app.MapOrderEndpoints();
app.MapSagaEndpoints();

app.Run();
