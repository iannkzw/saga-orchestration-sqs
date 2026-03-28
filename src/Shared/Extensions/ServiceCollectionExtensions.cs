using Amazon.SQS;
using Microsoft.Extensions.DependencyInjection;
using Shared.HealthChecks;

namespace Shared.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSagaConnectivity(this IServiceCollection services, string sqsServiceUrl)
    {
        services.AddSingleton<IAmazonSQS>(_ => new AmazonSQSClient(new AmazonSQSConfig
        {
            ServiceURL = sqsServiceUrl
        }));
        services.AddSingleton<SqsConnectivityCheck>();
        services.AddSingleton<PostgresConnectivityCheck>();
        services.AddSingleton<StartupConnectivityCheck>();
        services.AddHostedService(sp => sp.GetRequiredService<StartupConnectivityCheck>());
        return services;
    }
}
