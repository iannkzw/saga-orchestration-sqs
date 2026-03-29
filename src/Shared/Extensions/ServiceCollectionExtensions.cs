using Amazon.SQS;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Shared.HealthChecks;
using Shared.Idempotency;
using Shared.Telemetry;

namespace Shared.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSagaConnectivity(this IServiceCollection services, string sqsServiceUrl)
    {
        services.AddSingleton<IAmazonSQS>(_ => new AmazonSQSClient(new AmazonSQSConfig
        {
            ServiceURL = sqsServiceUrl
        }));
        services.AddSingleton<IdempotencyStore>();
        services.AddSingleton<SqsConnectivityCheck>();
        services.AddSingleton<PostgresConnectivityCheck>();
        services.AddSingleton<StartupConnectivityCheck>();
        services.AddHostedService(sp => sp.GetRequiredService<StartupConnectivityCheck>());
        return services;
    }

    public static IServiceCollection AddSagaTracing(this IServiceCollection services, string serviceName)
    {
        services.AddOpenTelemetry()
            .WithTracing(builder =>
            {
                builder
                    .AddSource(SagaActivitySource.Name)
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName))
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddConsoleExporter();

                var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
                if (!string.IsNullOrEmpty(otlpEndpoint))
                    builder.AddOtlpExporter();
            });

        return services;
    }
}
