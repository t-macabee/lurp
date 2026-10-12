using Lurp.PerfHarness;

namespace Lurp.Tests;

public sealed class SplitCommandLineTests
{
    public static TheoryData<string, string[]> Splits => new()
    {
        { @"dotnet ""C:\Program Files\x\Lurp.dll"" --mode=status", ["dotnet", @"C:\Program Files\x\Lurp.dll", "--mode=status"] },
        { "  a   b  ", ["a", "b"] },
        { "a \"\"", ["a", ""] },
        { "a\"b c\"d", ["ab cd"] }
    };

    [Theory]
    [MemberData(nameof(Splits))]
    public void SplitCommandLine_SplitsOnWhitespaceAndHonorsQuotes(string commandLine, string[] expected)
        => Assert.Equal(expected, ProcessRunner.SplitCommandLine(commandLine));

    public static TheoryData<string, string> Errors => new()
    {
        { "", "--lurp-cmd is empty." },
        { "   ", "--lurp-cmd is empty." },
        { "dotnet \"x", "--lurp-cmd has an unterminated double quote." }
    };

    [Theory]
    [MemberData(nameof(Errors))]
    public void SplitCommandLine_ThrowsHarnessError(string commandLine, string expectedMessage)
    {
        var exception = Assert.Throws<HarnessError>(() => ProcessRunner.SplitCommandLine(commandLine));
        Assert.Equal(expectedMessage, exception.Message);
    }
}
