using Outcome.Contracts;

namespace Outcome.Validation;

public sealed class OrderValidator : IOrderValidator
{
    public bool Validate(OrderDto order) => order.Total > 0;
}

public sealed class StrictOrderValidator : IOrderValidator
{
    public bool Validate(OrderDto order) => order.Total > 0 && !string.IsNullOrWhiteSpace(order.Note);
}
