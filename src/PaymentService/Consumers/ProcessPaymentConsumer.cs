using MassTransit;
using Shared.Contracts.Commands;
using Shared.Contracts.Events;

namespace PaymentService.Consumers;

public class ProcessPaymentConsumer : IConsumer<ProcessPayment>
{
    private readonly ILogger<ProcessPaymentConsumer> _logger;

    public ProcessPaymentConsumer(ILogger<ProcessPaymentConsumer> logger)
    {
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ProcessPayment> context)
    {
        var msg = context.Message;

        _logger.LogInformation(
            "[Payment] Processando pagamento CorrelationId={CorrelationId}, OrderId={OrderId}, Amount={Amount}",
            msg.CorrelationId, msg.OrderId, msg.Amount);

        await Task.Delay(200, context.CancellationToken);

        if (msg.Amount <= 0)
        {
            _logger.LogWarning(
                "[Payment] Pagamento rejeitado (valor invalido): CorrelationId={CorrelationId}, Amount={Amount}",
                msg.CorrelationId, msg.Amount);

            await context.Publish(new PaymentFailed(
                msg.CorrelationId,
                $"Valor de pagamento invalido: {msg.Amount}"));
            return;
        }

        var paymentId = Guid.NewGuid().ToString();

        _logger.LogInformation(
            "[Payment] Pagamento aprovado: CorrelationId={CorrelationId}, PaymentId={PaymentId}",
            msg.CorrelationId, paymentId);

        await context.Publish(new PaymentCompleted(
            msg.CorrelationId,
            paymentId,
            DateTime.UtcNow));
    }
}
