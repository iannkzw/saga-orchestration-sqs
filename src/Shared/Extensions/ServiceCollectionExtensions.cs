using Amazon.SQS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
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
                    .AddSource("Npgsql")
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName))
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
                if (!string.IsNullOrEmpty(otlpEndpoint))
                    builder.AddOtlpExporter();
                else
                    builder.AddConsoleExporter();
            });

        return services;
    }

    public static IServiceCollection AddSagaLogging(this IServiceCollection services, string serviceName)
    {
        services.AddLogging(logging =>
        {
            logging.AddOpenTelemetry(options =>
            {
                options.SetResourceBuilder(
                    ResourceBuilder.CreateDefault().AddService(serviceName));

                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;

                var otlpEndpoint = Environment.GetEnvironmentVariable(
                    "OTEL_EXPORTER_OTLP_ENDPOINT");

                if (!string.IsNullOrEmpty(otlpEndpoint))
                {
                    options.AddOtlpExporter(exporter =>
                    {
                        exporter.Endpoint = new Uri(otlpEndpoint);
                        exporter.Protocol = OtlpExportProtocol.Grpc;
                    });
                }
            });
        });

        return services;
    }
}
