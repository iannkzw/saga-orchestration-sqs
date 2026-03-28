using Shared.Extensions;
using Shared.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

var sqsServiceUrl = builder.Configuration["AWS_SERVICE_URL"] ?? "http://localhost:4566";
builder.Services.AddSagaConnectivity(sqsServiceUrl);

var app = builder.Build();

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

app.Run();
