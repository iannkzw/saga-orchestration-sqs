using MassTransit;
using Shared.Extensions;
using Shared.HealthChecks;
using ShippingService.Consumers;

var builder = WebApplication.CreateBuilder(args);

var sqsServiceUrl = builder.Configuration["AWS_SERVICE_URL"] ?? "http://localhost:4566";
builder.Services.AddSagaConnectivity(sqsServiceUrl);
builder.Services.AddSagaTracing("shipping-service");
builder.Services.AddSagaLogging("shipping-service");

builder.Services.AddMassTransit(cfg =>
{
    cfg.AddConsumer<ScheduleShippingConsumer, ScheduleShippingConsumerDefinition>();

    cfg.UsingAmazonSqs((context, sqsCfg) =>
    {
        sqsCfg.ConfigureSqsHost(builder.Configuration);
        sqsCfg.UseMessageRetry(r => r.Intervals(500, 1000, 2000));
        sqsCfg.ConfigureEndpoints(context);
    });
});

var app = builder.Build();

app.MapGet("/health", (StartupConnectivityCheck checks) => Results.Ok(new
{
    status = "healthy",
    service = "ShippingService",
    connections = new
    {
        sqs = checks.SqsHealthy,
        postgres = checks.PostgresHealthy
    }
}));

app.Run();
