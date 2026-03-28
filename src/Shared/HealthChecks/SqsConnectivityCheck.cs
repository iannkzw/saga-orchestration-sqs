using Amazon.SQS;
using Microsoft.Extensions.Logging;

namespace Shared.HealthChecks;

public class SqsConnectivityCheck
{
    private readonly IAmazonSQS _sqsClient;
    private readonly ILogger<SqsConnectivityCheck> _logger;

    public SqsConnectivityCheck(IAmazonSQS sqsClient, ILogger<SqsConnectivityCheck> logger)
    {
        _sqsClient = sqsClient;
        _logger = logger;
    }

    public async Task<bool> CheckAsync()
    {
        try
        {
            var response = await _sqsClient.ListQueuesAsync(string.Empty);
            _logger.LogInformation("SQS connectivity OK — {Count} queues found", response.QueueUrls.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SQS connectivity FAILED — service will continue without SQS");
            return false;
        }
    }
}
