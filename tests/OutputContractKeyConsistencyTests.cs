using System.Text.Json;

namespace Lurp.Tests;

public sealed class OutputContractKeyConsistencyTests
{
    [Theory]
    [InlineData("mcp:grep", true)]
    [InlineData("cli:grep:json", true)]
    [InlineData("mcp:search", false)]
    [InlineData("cli:search:json", false)]
    public void KeyConsistency_ReportsAnOmittedResultKey(string surface, bool shouldReport)
    {
        var checker = new OutputContractSnapshotTests.ObjectKeyConsistency();
        using var first = JsonDocument.Parse("""{"document":"A.cs","line":1}""");
        using var second = JsonDocument.Parse("""{"document":"B.cs"}""");

        checker.Record(surface, "$.results[]", first.RootElement);
        checker.Record(surface, "$.results[]", second.RootElement);

        if (shouldReport)
            Assert.Equal($"{surface} $.results[]: missing keys [line], extra keys []", Assert.Single(checker.Problems));
        else
            Assert.Empty(checker.Problems);
    }
}
