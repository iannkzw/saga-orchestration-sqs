using MassTransit;

namespace InventoryService.Consumers;

public class CancelInventoryConsumerDefinition : ConsumerDefinition<CancelInventoryConsumer>
{
    public CancelInventoryConsumerDefinition()
    {
        EndpointName = "cancel-inventory";
        ConcurrentMessageLimit = 4;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<CancelInventoryConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Intervals(500, 1000, 2000));
    }
}
