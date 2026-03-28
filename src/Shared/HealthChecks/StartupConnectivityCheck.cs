using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Shared.HealthChecks;

public class StartupConnectivityCheck : IHostedService
{
    private readonly SqsConnectivityCheck _sqsCheck;
    private readonly PostgresConnectivityCheck _pgCheck;
    private readonly ILogger<StartupConnectivityCheck> _logger;
    private bool _sqsHealthy;
    private bool _pgHealthy;

    public bool SqsHealthy => _sqsHealthy;
    public bool PostgresHealthy => _pgHealthy;

    public StartupConnectivityCheck(
        SqsConnectivityCheck sqsCheck,
        PostgresConnectivityCheck pgCheck,
        ILogger<StartupConnectivityCheck> logger)
    {
        _sqsCheck = sqsCheck;
        _pgCheck = pgCheck;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running startup connectivity checks...");
        _sqsHealthy = await _sqsCheck.CheckAsync();
        _pgHealthy = await _pgCheck.CheckAsync();
        _logger.LogInformation("Connectivity checks complete — SQS: {Sqs}, PostgreSQL: {Pg}",
            _sqsHealthy ? "OK" : "FAILED",
            _pgHealthy ? "OK" : "FAILED");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
