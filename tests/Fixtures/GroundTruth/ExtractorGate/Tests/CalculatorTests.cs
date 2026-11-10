using Xunit;

namespace ExtractorGate.Tests;

public class CalculatorTests
{
    [Fact]
    public void Add()
    {
        new ExtractorGate.App.Calculator().Add(1, 2);
    }
}
