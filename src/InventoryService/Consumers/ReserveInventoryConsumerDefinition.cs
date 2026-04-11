using MassTransit;

namespace InventoryService.Consumers;

public class ReserveInventoryConsumerDefinition : ConsumerDefinition<ReserveInventoryConsumer>
{
    public ReserveInventoryConsumerDefinition()
    {
        EndpointName = "reserve-inventory";
        ConcurrentMessageLimit = 4;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ReserveInventoryConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Intervals(500, 1000, 2000));
    }
}
