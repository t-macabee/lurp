using System.Globalization;
using Lurp.Handlers;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Storage;
using Lurp.Workspace;
using ModelContextProtocol;

namespace Lurp.Tests.Mcp;

public sealed class RequestErrorParityTests : IntegrationTestBase
{
    private async Task IndexFixtureAsync()
    {
        CreateProject("ReqProj", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                namespace ReqProj;

                public class Foo
                {
                    public void Bar() { }
                }
                """
        });
        await RunFullIndexAsync(DbPath);
    }

    [Fact]
    public async Task ImpactAndContext_InvalidInputs_GiveTheSameErrorOnBothSurfaces()
    {
        await IndexFixtureAsync();
        var outputDir = Path.GetDirectoryName(DbPath)!;
        await using var session = McpSessionContext.Create([$"--solution={SolutionPath}", $"--output-dir={outputDir}"]);
        var impactTool = new ImpactTool(session);
        var contextTool = new ContextTool(session);
        var sink = new CapturingOutputSink();

        void AssertSame(string flagName, string propertyName, string template, Action cliCall, Action mcpCall)
        {
            var cliEx = Assert.Throws<CliExitException>(cliCall);
            Assert.Equal("ERROR: " + string.Format(CultureInfo.InvariantCulture, template, flagName), cliEx.Message);

            var mcpEx = Assert.Throws<McpProtocolException>(mcpCall);
            Assert.Equal(McpErrorCode.InvalidParams, mcpEx.ErrorCode);
            Assert.Equal(string.Format(CultureInfo.InvariantCulture, template, propertyName), mcpEx.Message);
        }

        AssertSame(
            "--max-depth", "max_depth",
            "{0} must be a positive integer.",
            () => ImpactHandler.Run(["--symbol=ReqProj.Foo.Bar", "--max-depth=0", $"--output-dir={outputDir}"], sink),
            () => impactTool.LurpImpact(symbol: "ReqProj.Foo.Bar", max_depth: 0));

        AssertSame(
            "--kinds", "kinds",
            "{0} contains an empty item.",
            () => ImpactHandler.Run(["--symbol=ReqProj.Foo.Bar", "--kinds=Calls,", $"--output-dir={outputDir}"], sink),
            () => impactTool.LurpImpact(symbol: "ReqProj.Foo.Bar", kinds: ["Calls,"]));

        AssertSame(
            "--provenance", "provenance",
            $"Unknown provenance 'bogus' in {{0}}. Use one of: {string.Join(", ", Provenance.All)} (case-insensitive).",
            () => ImpactHandler.Run(["--symbol=ReqProj.Foo.Bar", "--provenance=bogus", $"--output-dir={outputDir}"], sink),
            () => impactTool.LurpImpact(symbol: "ReqProj.Foo.Bar", provenance: ["bogus"]));

        AssertSame(
            "--direction", "direction",
            "Invalid direction 'x'. Use 'upstream' or 'downstream'.",
            () => ImpactHandler.Run(["--symbol=ReqProj.Foo.Bar", "--direction=x", $"--output-dir={outputDir}"], sink),
            () => impactTool.LurpImpact(symbol: "ReqProj.Foo.Bar", direction: "x"));

        AssertSame(
            "--tier", "tier",
            $"unknown {{0}} 'nope'. Valid tiers: {string.Join(", ", ContextAssembler.TierNames)}.",
            () => ContextHandler.Run(["--symbol=ReqProj.Foo.Bar", "--tier=nope", $"--output-dir={outputDir}"], sink),
            () => contextTool.LurpContext(symbol: "ReqProj.Foo.Bar", tier: "nope"));

        AssertSame(
            "--intent", "intent",
            "{0} must be one of: inspect, modify, diagnose.",
            () => ContextHandler.Run(["--symbol=ReqProj.Foo.Bar", "--intent=x", $"--output-dir={outputDir}"], sink),
            () => contextTool.LurpContext(symbol: "ReqProj.Foo.Bar", intent: "x"));

        AssertSame(
            "--max-hops", "max_hops",
            "{0} must be a positive integer.",
            () => ContextHandler.Run(["--symbol=ReqProj.Foo.Bar", "--max-hops=0", $"--output-dir={outputDir}"], sink),
            () => contextTool.LurpContext(symbol: "ReqProj.Foo.Bar", max_hops: 0));
    }
}
