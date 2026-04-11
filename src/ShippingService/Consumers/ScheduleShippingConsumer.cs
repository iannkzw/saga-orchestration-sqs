using MassTransit;
using Shared.Contracts.Commands;
using Shared.Contracts.Events;

namespace ShippingService.Consumers;

public class ScheduleShippingConsumer : IConsumer<ScheduleShipping>
{
    private readonly ILogger<ScheduleShippingConsumer> _logger;

    public ScheduleShippingConsumer(ILogger<ScheduleShippingConsumer> logger)
    {
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ScheduleShipping> context)
    {
        var msg = context.Message;

        _logger.LogInformation(
            "[Shipping] Agendando entrega CorrelationId={CorrelationId}, OrderId={OrderId}, Items={Items}",
            msg.CorrelationId, msg.OrderId, msg.Items.Count);

        await Task.Delay(200, context.CancellationToken);

        if (msg.Items.Count == 0)
        {
            _logger.LogWarning(
                "[Shipping] Agendamento falhou (sem itens): CorrelationId={CorrelationId}",
                msg.CorrelationId);

            await context.Publish(new ShippingFailed(
                msg.CorrelationId,
                "Pedido sem itens para entrega"));
            return;
        }

        var trackingCode = $"TRK-{Guid.NewGuid().ToString("N")[..12].ToUpperInvariant()}";

        _logger.LogInformation(
            "[Shipping] Entrega agendada: CorrelationId={CorrelationId}, TrackingCode={TrackingCode}",
            msg.CorrelationId, trackingCode);

        await context.Publish(new ShippingScheduled(
            msg.CorrelationId,
            trackingCode,
            DateTime.UtcNow));
    }
}
