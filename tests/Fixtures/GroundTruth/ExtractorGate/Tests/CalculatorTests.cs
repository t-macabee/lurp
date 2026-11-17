using Xunit;

namespace ExtractorGate.Tests;

public sealed class SlowFactAttribute : FactAttribute { }

public static class CalculatorHelper
{
    public static int Run() => new ExtractorGate.App.Calculator().Add(3, 3);
}

public class CalculatorTests
{
    [Fact]
    public void Add()
    {
        new ExtractorGate.App.Calculator().Add(1, 2);
    }

    [SlowFact]
    public void AddSlow()
    {
        new ExtractorGate.App.Calculator().Add(2, 2);
    }

    [Fact]
    public void AddViaHelper()
    {
        CalculatorHelper.Run();
    }
}
