using MassTransit;
using OrderService.Data;
using OrderService.Models;

namespace OrderService.StateMachine;

public class OrderStateMachineDefinition : SagaDefinition<OrderSagaInstance>
{
    public OrderStateMachineDefinition()
    {
        EndpointName = "order-saga";
        ConcurrentMessageLimit = 8;
    }

    protected override void ConfigureSaga(IReceiveEndpointConfigurator endpointConfigurator,
        ISagaConfigurator<OrderSagaInstance> sagaConfigurator,
        IRegistrationContext context)
    {
        sagaConfigurator.UseMessageRetry(r => r.Intervals(500, 1000, 2000));
        endpointConfigurator.UseEntityFrameworkOutbox<OrderDbContext>(context);
    }
}
