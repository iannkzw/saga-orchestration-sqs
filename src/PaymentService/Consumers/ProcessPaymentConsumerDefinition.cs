using MassTransit;
using Shared.Contracts.Commands;

namespace PaymentService.Consumers;

public class ProcessPaymentConsumerDefinition : ConsumerDefinition<ProcessPaymentConsumer>
{
    public ProcessPaymentConsumerDefinition()
    {
        EndpointName = "process-payment";
        ConcurrentMessageLimit = 4;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ProcessPaymentConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Intervals(500, 1000, 2000));
    }
}
