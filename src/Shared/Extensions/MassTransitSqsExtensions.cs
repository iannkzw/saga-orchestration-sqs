using Amazon;
using MassTransit;
using Microsoft.Extensions.Configuration;

namespace Shared.Extensions;

public static class MassTransitSqsExtensions
{
    /// <summary>
    /// Configura o host SQS do MassTransit lendo AWS_SQS_SERVICE_URL e AWS_REGION do IConfiguration.
    /// Padroes: http://localstack:4566 e us-east-1.
    /// </summary>
    public static void ConfigureSqsHost(this IAmazonSqsBusFactoryConfigurator cfg, IConfiguration configuration)
    {
        var serviceUrl = configuration["AWS_SQS_SERVICE_URL"] ?? "http://localstack:4566";
        var region = configuration["AWS_REGION"] ?? "us-east-1";

        cfg.Host(new Uri($"amazonsqs://{region}"), h =>
        {
            h.AccessKey("test");
            h.SecretKey("test");
            h.Config(new Amazon.SQS.AmazonSQSConfig
            {
                ServiceURL = serviceUrl,
                AuthenticationRegion = region,
            });
        });
    }
}
