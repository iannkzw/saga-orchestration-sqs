namespace SagaOrchestrator;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SagaOrchestrator worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            // SQS message polling will be implemented in M2
            await Task.Delay(5000, stoppingToken);
        }
    }
}
