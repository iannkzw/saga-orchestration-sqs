using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Shared.Idempotency;

public class IdempotencyStore
{
    private readonly string? _connectionString;
    private readonly ILogger<IdempotencyStore> _logger;

    public IdempotencyStore(IConfiguration configuration, ILogger<IdempotencyStore> logger)
    {
        _connectionString = configuration.GetConnectionString("SagaDb");
        _logger = logger;
    }

    public async Task EnsureTableAsync()
    {
        if (_connectionString is null) return;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS idempotency_keys (
                idempotency_key VARCHAR(256) PRIMARY KEY,
                saga_id         UUID NOT NULL,
                result_json     TEXT NOT NULL,
                created_at      TIMESTAMP NOT NULL DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS idx_idempotency_saga_id ON idempotency_keys (saga_id);
            """;
        await cmd.ExecuteNonQueryAsync();

        _logger.LogInformation("Tabela idempotency_keys garantida no PostgreSQL");
    }

    public async Task<T?> TryGetAsync<T>(string idempotencyKey) where T : class
    {
        if (_connectionString is null) return null;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT result_json FROM idempotency_keys WHERE idempotency_key = @key";
        cmd.Parameters.AddWithValue("key", idempotencyKey);

        var result = await cmd.ExecuteScalarAsync();
        if (result is null or DBNull) return null;

        _logger.LogInformation("Idempotency hit: chave {Key} ja processada, retornando resultado anterior", idempotencyKey);
        return JsonSerializer.Deserialize<T>((string)result);
    }

    public async Task SaveAsync<T>(string idempotencyKey, Guid sagaId, T result)
    {
        if (_connectionString is null) return;

        var json = JsonSerializer.Serialize(result);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO idempotency_keys (idempotency_key, saga_id, result_json)
            VALUES (@key, @sagaId, @json)
            ON CONFLICT (idempotency_key) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("key", idempotencyKey);
        cmd.Parameters.AddWithValue("sagaId", sagaId);
        cmd.Parameters.AddWithValue("json", json);

        var rowsAffected = await cmd.ExecuteNonQueryAsync();
        if (rowsAffected > 0)
            _logger.LogInformation("Idempotency save: chave {Key} salva para saga {SagaId}", idempotencyKey, sagaId);
        else
            _logger.LogDebug("Idempotency save: chave {Key} ja existia (ON CONFLICT), ignorado", idempotencyKey);
    }
}
