using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace InventoryService;

public class InventoryRepository
{
    private readonly string _connectionString;
    private readonly ILogger<InventoryRepository> _logger;

    public InventoryRepository(IConfiguration configuration, ILogger<InventoryRepository> logger)
    {
        _connectionString = configuration.GetConnectionString("SagaDb")
            ?? "Host=postgres;Port=5432;Database=saga_db;Username=saga;Password=saga_pass";
        _logger = logger;
    }

    public async Task EnsureTablesAsync()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS inventory (
                product_id  VARCHAR(100) PRIMARY KEY,
                name        VARCHAR(255) NOT NULL,
                quantity    INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS inventory_reservations (
                reservation_id  VARCHAR(256) PRIMARY KEY,
                product_id      VARCHAR(100) NOT NULL REFERENCES inventory(product_id),
                quantity        INTEGER NOT NULL,
                saga_id         UUID NOT NULL,
                reserved_at     TIMESTAMP NOT NULL DEFAULT NOW()
            );

            INSERT INTO inventory (product_id, name, quantity)
            VALUES ('PROD-001', 'Produto Demo Concorrencia', 2)
            ON CONFLICT (product_id) DO NOTHING;
            """;
        await cmd.ExecuteNonQueryAsync();

        _logger.LogInformation(
            "Tabelas inventory e inventory_reservations garantidas. " +
            "Produto PROD-001 inserido com estoque inicial = 2 (se ainda nao existia).");
    }

    /// <summary>
    /// Tenta reservar estoque para um produto.
    /// Quando useLock=true: usa SELECT FOR UPDATE (locking pessimista).
    /// Quando useLock=false: leitura sem lock + delay artificial para expor race condition.
    /// </summary>
    public async Task<(bool Success, string? ErrorMessage)> TryReserveAsync(
        string productId,
        int quantity,
        string reservationId,
        Guid sagaId,
        bool useLock,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            var selectSql = useLock
                ? "SELECT quantity FROM inventory WHERE product_id = @productId FOR UPDATE"
                : "SELECT quantity FROM inventory WHERE product_id = @productId";

            await using var selectCmd = conn.CreateCommand();
            selectCmd.Transaction = tx;
            selectCmd.CommandText = selectSql;
            selectCmd.Parameters.AddWithValue("productId", productId);

            var currentStock = (int?)await selectCmd.ExecuteScalarAsync(ct);

            if (currentStock is null)
            {
                await tx.RollbackAsync(ct);
                return (false, $"Produto '{productId}' nao encontrado no inventario");
            }

            _logger.LogInformation(
                "[Inventory] SELECT{Lock}: produto={ProductId}, estoque_lido={Stock}, solicitado={Qty}",
                useLock ? " FOR UPDATE" : " (sem lock)", productId, currentStock, quantity);

            if (!useLock)
            {
                // Simula janela TOCTOU: outra transacao pode alterar o estoque entre
                // o SELECT e o UPDATE quando nao ha lock.
                await Task.Delay(150, ct);
            }

            if (currentStock < quantity)
            {
                await tx.RollbackAsync(ct);
                _logger.LogWarning(
                    "[Inventory] Estoque insuficiente: produto={ProductId}, disponivel={Stock}, solicitado={Qty}",
                    productId, currentStock, quantity);
                return (false, $"Estoque insuficiente: disponivel={currentStock}, solicitado={quantity}");
            }

            await using var updateCmd = conn.CreateCommand();
            updateCmd.Transaction = tx;
            updateCmd.CommandText =
                "UPDATE inventory SET quantity = quantity - @qty WHERE product_id = @productId";
            updateCmd.Parameters.AddWithValue("qty", quantity);
            updateCmd.Parameters.AddWithValue("productId", productId);
            await updateCmd.ExecuteNonQueryAsync(ct);

            await using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO inventory_reservations (reservation_id, product_id, quantity, saga_id)
                VALUES (@reservationId, @productId, @quantity, @sagaId)
                """;
            insertCmd.Parameters.AddWithValue("reservationId", reservationId);
            insertCmd.Parameters.AddWithValue("productId", productId);
            insertCmd.Parameters.AddWithValue("quantity", quantity);
            insertCmd.Parameters.AddWithValue("sagaId", sagaId);
            await insertCmd.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "[Inventory] Reserva confirmada: produto={ProductId}, qty={Qty}, reservationId={ReservationId}",
                productId, quantity, reservationId);

            return (true, null);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex, "[Inventory] Erro ao reservar estoque: produto={ProductId}", productId);
            return (false, $"Erro interno: {ex.Message}");
        }
    }

    /// <summary>
    /// Libera uma reserva existente, restaurando o estoque no produto.
    /// </summary>
    public async Task<bool> ReleaseAsync(string reservationId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            await using var selectCmd = conn.CreateCommand();
            selectCmd.Transaction = tx;
            selectCmd.CommandText =
                "SELECT product_id, quantity FROM inventory_reservations WHERE reservation_id = @id FOR UPDATE";
            selectCmd.Parameters.AddWithValue("id", reservationId);

            await using var reader = await selectCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                await reader.CloseAsync();
                await tx.RollbackAsync(ct);
                _logger.LogWarning("[Inventory] Reserva nao encontrada para liberar: {ReservationId}", reservationId);
                return false;
            }

            var productId = reader.GetString(0);
            var quantity = reader.GetInt32(1);
            await reader.CloseAsync();

            await using var updateCmd = conn.CreateCommand();
            updateCmd.Transaction = tx;
            updateCmd.CommandText =
                "UPDATE inventory SET quantity = quantity + @qty WHERE product_id = @productId";
            updateCmd.Parameters.AddWithValue("qty", quantity);
            updateCmd.Parameters.AddWithValue("productId", productId);
            await updateCmd.ExecuteNonQueryAsync(ct);

            await using var deleteCmd = conn.CreateCommand();
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText =
                "DELETE FROM inventory_reservations WHERE reservation_id = @id";
            deleteCmd.Parameters.AddWithValue("id", reservationId);
            await deleteCmd.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "[Inventory] Reserva liberada: {ReservationId}, produto={ProductId}, qty={Qty} devolvido ao estoque",
                reservationId, productId, quantity);

            return true;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex, "[Inventory] Erro ao liberar reserva {ReservationId}", reservationId);
            return false;
        }
    }

    public async Task<int?> GetStockAsync(string productId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT quantity FROM inventory WHERE product_id = @productId";
        cmd.Parameters.AddWithValue("productId", productId);

        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : (int)result;
    }

    public async Task ResetStockAsync(string productId, int quantity)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE inventory SET quantity = @quantity WHERE product_id = @productId;
            DELETE FROM inventory_reservations WHERE product_id = @productId;
            """;
        cmd.Parameters.AddWithValue("quantity", quantity);
        cmd.Parameters.AddWithValue("productId", productId);
        await cmd.ExecuteNonQueryAsync();

        _logger.LogInformation(
            "[Inventory] Estoque resetado: produto={ProductId}, quantidade={Quantity}",
            productId, quantity);
    }
}
