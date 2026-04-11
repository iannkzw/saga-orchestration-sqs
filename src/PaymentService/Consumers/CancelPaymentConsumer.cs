using MassTransit;
using Shared.Contracts.Commands;
using Shared.Contracts.Events;

namespace PaymentService.Consumers;

public class CancelPaymentConsumer : IConsumer<CancelPayment>
{
    private readonly ILogger<CancelPaymentConsumer> _logger;

    public CancelPaymentConsumer(ILogger<CancelPaymentConsumer> logger)
    {
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<CancelPayment> context)
    {
        var msg = context.Message;

        _logger.LogInformation(
            "[Payment] Compensando pagamento CorrelationId={CorrelationId}, PaymentId={PaymentId}",
            msg.CorrelationId, msg.PaymentId);

        await Task.Delay(200, context.CancellationToken);

        _logger.LogInformation(
            "[Payment] Pagamento estornado: CorrelationId={CorrelationId}, PaymentId={PaymentId}",
            msg.CorrelationId, msg.PaymentId);

        await context.Publish(new PaymentCancelled(
            msg.CorrelationId,
            msg.PaymentId));
    }
}
