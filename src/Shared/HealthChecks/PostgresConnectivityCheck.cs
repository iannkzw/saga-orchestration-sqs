using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Shared.HealthChecks;

public class PostgresConnectivityCheck
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<PostgresConnectivityCheck> _logger;

    public PostgresConnectivityCheck(IConfiguration configuration, ILogger<PostgresConnectivityCheck> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<bool> CheckAsync()
    {
        try
        {
            var connectionString = _configuration.GetConnectionString("SagaDb");
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            _logger.LogInformation("PostgreSQL connectivity OK — connected to {Database}", connection.Database);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PostgreSQL connectivity FAILED — service will continue without database");
            return false;
        }
    }
}
