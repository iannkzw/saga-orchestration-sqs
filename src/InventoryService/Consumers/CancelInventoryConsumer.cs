using MassTransit;
using Shared.Contracts.Commands;
using Shared.Contracts.Events;

namespace InventoryService.Consumers;

public class CancelInventoryConsumer : IConsumer<CancelInventory>
{
    private readonly InventoryRepository _repository;
    private readonly ILogger<CancelInventoryConsumer> _logger;

    public CancelInventoryConsumer(
        InventoryRepository repository,
        ILogger<CancelInventoryConsumer> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<CancelInventory> context)
    {
        var msg = context.Message;

        _logger.LogInformation(
            "[Inventory] Compensando reserva CorrelationId={CorrelationId}, ReservationId={ReservationId}",
            msg.CorrelationId, msg.ReservationId);

        var released = await _repository.ReleaseAsync(msg.ReservationId, context.CancellationToken);

        if (released)
        {
            _logger.LogInformation(
                "[Inventory] Reserva liberada CorrelationId={CorrelationId}, ReservationId={ReservationId}",
                msg.CorrelationId, msg.ReservationId);
        }
        else
        {
            _logger.LogWarning(
                "[Inventory] Reserva nao encontrada ao liberar (idempotente) CorrelationId={CorrelationId}, ReservationId={ReservationId}",
                msg.CorrelationId, msg.ReservationId);
        }

        await context.Publish(new InventoryCancelled(
            msg.CorrelationId,
            msg.ReservationId));
    }
}
