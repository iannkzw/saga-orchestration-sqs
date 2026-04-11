using MassTransit;

namespace ShippingService.Consumers;

public class ScheduleShippingConsumerDefinition : ConsumerDefinition<ScheduleShippingConsumer>
{
    public ScheduleShippingConsumerDefinition()
    {
        EndpointName = "schedule-shipping";
        ConcurrentMessageLimit = 4;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ScheduleShippingConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Intervals(500, 1000, 2000));
    }
}
