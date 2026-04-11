using MassTransit;

namespace PaymentService.Consumers;

public class CancelPaymentConsumerDefinition : ConsumerDefinition<CancelPaymentConsumer>
{
    public CancelPaymentConsumerDefinition()
    {
        EndpointName = "cancel-payment";
        ConcurrentMessageLimit = 4;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<CancelPaymentConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Intervals(500, 1000, 2000));
    }
}
