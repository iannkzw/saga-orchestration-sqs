using Amazon;
using MassTransit;
using Microsoft.Extensions.Configuration;

namespace Shared.Extensions;

public static class MassTransitSqsExtensions
{
    /// <summary>
    /// Configura o host SQS do MassTransit lendo AWS_SERVICE_URL, AWS_DEFAULT_REGION,
    /// AWS_ACCESS_KEY_ID e AWS_SECRET_ACCESS_KEY do IConfiguration.
    /// Padroes: http://localstack:4566 e us-east-1.
    /// </summary>
    public static void ConfigureSqsHost(this IAmazonSqsBusFactoryConfigurator cfg, IConfiguration configuration)
    {
        var serviceUrl = configuration["AWS_SERVICE_URL"] ?? "http://localstack:4566";
        var region = configuration["AWS_DEFAULT_REGION"] ?? "us-east-1";
        var accessKey = configuration["AWS_ACCESS_KEY_ID"] ?? "test";
        var secretKey = configuration["AWS_SECRET_ACCESS_KEY"] ?? "test";

        cfg.Host(new Uri($"amazonsqs://{region}"), h =>
        {
            h.AccessKey(accessKey);
            h.SecretKey(secretKey);
            h.Config(new Amazon.SQS.AmazonSQSConfig
            {
                ServiceURL = serviceUrl,
                AuthenticationRegion = region,
            });
        });
    }
}
