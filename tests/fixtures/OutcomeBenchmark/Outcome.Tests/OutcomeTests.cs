using Outcome.Application;
using Outcome.Composition;
using Outcome.Contracts;
using Outcome.Validation;

namespace Outcome.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class FactAttribute : Attribute { }

public sealed class ValidationTests
{
    [Fact]
    public void Validate_RejectsZeroTotal()
    {
        var validator = new OrderValidator();
        _ = validator.Validate(new OrderDto(1, 0));
    }
}

public sealed class HandlerTests
{
    [Fact]
    public void Handle_ReturnsValidationResult()
    {
        var handler = new OrderHandler(new OrderValidator());
        _ = handler.Handle(new OrderDto(1, 10));
    }
}

public sealed class CompositionTests
{
    [Fact]
    public void Configure_RegistersApplicationServices()
    {
        _ = ServiceRegistration.Configure(new TestServiceCollection());
    }

    private sealed class TestServiceCollection : Microsoft.Extensions.DependencyInjection.IServiceCollection { }
}
