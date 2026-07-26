using Outcome.Contracts;

namespace Outcome.Application;

public sealed class OrderHandler(IOrderValidator validator) : IOrderHandler
{
    public OrderResult Handle(OrderDto order)
    {
        var accepted = validator.Validate(order);
        return new OrderResult(order.Id, accepted);
    }
}
