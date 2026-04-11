using MassTransit;
using Shared.Extensions;
using Shared.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Worker legado mantido até mt-consumers ser implementado
builder.Services.AddHostedService<PaymentService.Worker>();
var sqsServiceUrl = builder.Configuration["AWS_SERVICE_URL"] ?? "http://localhost:4566";
builder.Services.AddSagaConnectivity(sqsServiceUrl);
builder.Services.AddSagaTracing("payment-service");
builder.Services.AddSagaLogging("payment-service");

builder.Services.AddMassTransit(cfg =>
{
    // Consumers registrados em mt-consumers: ProcessPaymentConsumer, RefundPaymentConsumer

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
    service = "PaymentService",
    connections = new
    {
        sqs = checks.SqsHealthy,
        postgres = checks.PostgresHealthy
    }
}));

app.Run();
