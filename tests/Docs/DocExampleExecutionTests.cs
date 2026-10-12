using Lurp.Tests.Docs;
using Microsoft.Build.Locator;

namespace Lurp.Tests;

/// <summary>
///     Tier 2 of the doc-example test: indexes one fixture and runs every
///     <c>lurp …</c> command found in the docs that is not <c>index</c> or
///     <c>serve</c>. Placeholder values are replaced from a test-owned table; a
///     placeholder that survives is a failure, and so is any exception.
/// </summary>
public sealed class DocExampleExecutionTests : IntegrationTestBase
{
    private const string ChainSource = """
                                       namespace TestProject;

                                       public class Chain
                                       {
                                           public void M0() { }
                                           public void M1() => M0();
                                           public void M2() => M1();
                                           public void M3() => M2();
                                           public void M4() => M3();
                                           public void M5() => M4();
                                       }
                                       """;

    private const string ChainFile = "src/TestProject/Chain.cs";

    [SkippableFact]
    public async Task EveryRunnableExample_RunsOnTheFixture()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");
        CreateProject("TestProject", new Dictionary<string, string> { ["Chain.cs"] = ChainSource });
        var snapshotId = await RunFullIndexAsync(DbPath);

        var outputDir = Path.GetDirectoryName(DbPath)!;
        var substitutions = BuildSubstitutions(snapshotId, outputDir);
        var root = DocCatalog.RepoRoot();
        var failures = new List<string>();
        var runs = 0;

        foreach (var example in DocExamples.ExtractAll(root))
        {
            if (example.Kind != DocExampleKind.Command || example.SkipReason is not null)
                continue;

            var mode = DocExamples.ModeOf(DocExamples.Tokens(example.Text));
            if (mode is null or "index" or "serve")
                continue;

            var args = Substitute(example, substitutions, out var placeholderFailure);
            if (placeholderFailure is not null)
            {
                failures.Add(placeholderFailure);
                continue;
            }

            // The docs show some snippets without a database location (they
            // illustrate one flag inside a generated table row). Tier 2 adds the
            // fixture's own output directory so the run reaches the handler.
            if (!args.Any(arg => arg.StartsWith("--output-dir=", StringComparison.Ordinal)))
                args = [.. args, $"--output-dir={outputDir}"];

            try
            {
                await CliModeRunner.RunAsync(mode, args);
                runs++;
            }
            catch (Exception ex)
            {
                failures.Add($"{example.File}:{example.Line}: {ex.Message}");
            }
        }

        Assert.True(runs > 0, "No runnable example was executed; the fixture or the extractor is wrong.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private List<(string Prefix, string Value)> BuildSubstitutions(string snapshotId, string outputDir)
    {
        var chainLine = Array.FindIndex(ChainSource.Split('\n'), line => line.Contains("public class Chain", StringComparison.Ordinal)) + 1;
        Assert.True(chainLine > 0, "The fixture source no longer contains 'public class Chain'.");

        return
        [
            ("--output-dir=", outputDir),
            ("--solution=", SolutionPath),
            ("--file=", ChainFile),
            ("--line=", chainLine.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("--document=", ChainFile),
            ("--symbol=", "T:TestProject.Chain"),
            ("--from-snapshot=", snapshotId),
            ("--to-snapshot=", snapshotId),
            ("--snapshot=", snapshotId),
            ("--query=", "Chain"),
        ];
    }

    private static string[] Substitute(DocExample example, List<(string Prefix, string Value)> table, out string? failure)
    {
        failure = null;
        var tokens = DocExamples.Tokens(example.Text);

        for (var i = 0; i < tokens.Count; i++)
        {
            foreach (var (prefix, value) in table)
            {
                if (!tokens[i].StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                tokens[i] = prefix + value;
                break;
            }

            var separator = tokens[i].IndexOf('=');
            if (separator < 0)
                continue;
            var current = tokens[i][(separator + 1)..];
            if (current.StartsWith('<'))
            {
                failure = $"unsubstituted placeholder {current} in {example.File}:{example.Line}";
                return [.. tokens];
            }
        }

        return [.. tokens];
    }
}
