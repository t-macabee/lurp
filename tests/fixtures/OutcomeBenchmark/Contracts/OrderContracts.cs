namespace Outcome.Contracts;

public record OrderDto(int Id, decimal Total, string? Note = null);

public record OrderResult(int Id, bool Accepted);

public interface IOrderValidator
{
    bool Validate(OrderDto order);
}

public interface IOrderHandler
{
    OrderResult Handle(OrderDto order);
}
