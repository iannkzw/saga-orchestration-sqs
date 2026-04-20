using MassTransit;
using Shared.Contracts.Commands;
using Shared.Contracts.Events;

namespace InventoryService.Consumers;

public class ReserveInventoryConsumer : IConsumer<ReserveInventory>
{
    private readonly InventoryRepository _repository;
    private readonly ILogger<ReserveInventoryConsumer> _logger;
    private readonly string _lockingMode;
    private readonly int _optimisticMaxRetries;

    public ReserveInventoryConsumer(
        InventoryRepository repository,
        IConfiguration configuration,
        ILogger<ReserveInventoryConsumer> logger)
    {
        _repository = repository;
        _logger = logger;

        // INVENTORY_LOCKING_MODE=pessimistic -> SELECT FOR UPDATE (padrao)
        // INVENTORY_LOCKING_MODE=optimistic  -> version check + retry automatico
        // INVENTORY_LOCKING_MODE=none        -> sem lock + delay (demonstra race condition)
        // Fallback: INVENTORY_LOCKING_ENABLED=true/false (compat M5)
        var lockingMode = configuration.GetValue<string>("INVENTORY_LOCKING_MODE");
        if (string.IsNullOrEmpty(lockingMode))
        {
            var legacyEnabled = configuration.GetValue<bool>("INVENTORY_LOCKING_ENABLED", true);
            lockingMode = legacyEnabled ? "pessimistic" : "none";
        }
        _lockingMode = lockingMode.ToLowerInvariant();
        _optimisticMaxRetries = configuration.GetValue<int>("INVENTORY_OPTIMISTIC_MAX_RETRIES", 3);
    }

    public async Task Consume(ConsumeContext<ReserveInventory> context)
    {
        var msg = context.Message;

        _logger.LogInformation(
            "[Inventory] Reservando estoque CorrelationId={CorrelationId}, OrderId={OrderId}, Items={Items}, LockMode={LockMode}",
            msg.CorrelationId, msg.OrderId, msg.Items.Count, _lockingMode);

        if (msg.SimulateFailure == "inventory")
        {
            _logger.LogWarning(
                "[Inventory] Falha simulada via X-Simulate-Failure: CorrelationId={CorrelationId}",
                msg.CorrelationId);

            await context.Publish(new InventoryFailed(
                msg.CorrelationId,
                "Simulated inventory failure"));
            return;
        }

        var firstItem = msg.Items.FirstOrDefault();
        if (firstItem is null)
        {
            await context.Publish(new InventoryFailed(
                msg.CorrelationId,
                "Nenhum item no pedido"));
            return;
        }

        var reservationId = Guid.NewGuid().ToString();

        var (success, errorMessage) = _lockingMode switch
        {
            "optimistic" => await _repository.TryReserveOptimisticAsync(
                firstItem.ProductId, firstItem.Quantity, reservationId, msg.CorrelationId,
                _optimisticMaxRetries, context.CancellationToken),
            "pessimistic" => await _repository.TryReserveAsync(
                firstItem.ProductId, firstItem.Quantity, reservationId, msg.CorrelationId,
                useLock: true, context.CancellationToken),
            _ => await _repository.TryReserveAsync(
                firstItem.ProductId, firstItem.Quantity, reservationId, msg.CorrelationId,
                useLock: false, context.CancellationToken)
        };

        if (success)
        {
            _logger.LogInformation(
                "[Inventory] Reserva confirmada CorrelationId={CorrelationId}, ReservationId={ReservationId}",
                msg.CorrelationId, reservationId);

            await context.Publish(new InventoryReserved(
                msg.CorrelationId,
                reservationId,
                DateTime.UtcNow));
        }
        else
        {
            _logger.LogWarning(
                "[Inventory] Reserva falhou CorrelationId={CorrelationId}, Reason={Reason}",
                msg.CorrelationId, errorMessage);

            await context.Publish(new InventoryFailed(
                msg.CorrelationId,
                errorMessage ?? "Falha desconhecida ao reservar estoque"));
        }
    }
}
