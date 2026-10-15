using System.Text.RegularExpressions;

namespace Lurp.Tests.Mcp;

public sealed class WorkspaceLoaderNoConsoleTests
{
    [Fact]
    public void WorkspaceLoader_HasNoRawConsoleWrites()
    {
        var repoRoot = TestRepoRoot.Find();
        var loaderPath = Path.Combine(repoRoot, "src", "Workspace", "WorkspaceLoader.cs");
        Assert.True(File.Exists(loaderPath), $"WorkspaceLoader.cs not found at {loaderPath}");

        var text = File.ReadAllText(loaderPath);
        var consolePattern = new Regex(@"Console\.", RegexOptions.Compiled);
        var hits = new List<string>();
        var lines = text.Split('\n');
        foreach (Match m in consolePattern.Matches(text))
        {
            var line = text[..m.Index].Count(c => c == '\n') + 1;
            var lineText = lines[Math.Max(0, line - 1)];
            // Allow references in XML doc comments (e.g., <see cref="Console.Write"/>)
            if (lineText.Contains("<see") && lineText.Contains("Console.")) continue;
            hits.Add($"WorkspaceLoader.cs:{line}: {lineText.Trim()}");
        }

        Assert.True(hits.Count == 0, $"WorkspaceLoader must not contain raw Console calls (route via IOutputSink instead):\n{string.Join("\n", hits)}");
    }

    [Fact]
    public void IndexRunner_PassesSinkToWorkspaceLoader()
    {
        var repoRoot = TestRepoRoot.Find();
        var indexRunnerPath = Path.Combine(repoRoot, "src", "Workspace", "IndexRunner.cs");
        Assert.True(File.Exists(indexRunnerPath), $"IndexRunner.cs not found at {indexRunnerPath}");

        var text = File.ReadAllText(indexRunnerPath);
        Assert.Contains("new WorkspaceLoader(sink)", text);
        Assert.DoesNotContain("new WorkspaceLoader()", text);
    }
}
