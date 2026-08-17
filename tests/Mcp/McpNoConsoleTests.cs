using System.Text.RegularExpressions;

namespace Lurp.Tests.Mcp;

public sealed class McpNoConsoleTests
{
    [Fact]
    public void McpSource_ContainsExactlyOneConsoleCall_AllowedPreHandshake()
    {
        var mcpDir = FindMcpDirectory();
        var files = Directory.GetFiles(mcpDir, "*.cs", SearchOption.AllDirectories);
        var consolePattern = new Regex(@"Console\.", RegexOptions.Compiled);

        var hits = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var lines = text.Split('\n');
            foreach (Match m in consolePattern.Matches(text))
            {
                var line = text[..m.Index].Count(c => c == '\n') + 1;
                var lineText = lines[Math.Max(0, line - 1)];
                hits.Add($"{Path.GetFileName(file)}:{line}: {lineText.Trim()}");
            }
        }

        // Exactly one console call is sanctioned: McpSessionContext.cs before handshake
        Assert.Single(hits);
        Assert.Contains("McpSessionContext", hits[0]);
        Assert.Contains("Console.Error", hits[0]);
    }

    private static string FindMcpDirectory()
    {
        // Walk from test assembly location to repo root
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "src", "Mcp");
            if (Directory.Exists(candidate))
                return candidate;
            // Also check workspace-level src/Mcp relative to repo root discovered via project
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        // Fallback: locate via current test file's repo detection
        // Use known path from test run: tests are under <repo>/tests
        var repoTests = Path.Combine(Directory.GetCurrentDirectory(), "src", "Mcp");
        if (Directory.Exists(repoTests)) return repoTests;

        // Search upwards for index.db or .git
        var cur = Directory.GetCurrentDirectory();
        while (cur != null)
        {
            if (File.Exists(Path.Combine(cur, "src", "Lurp.csproj")) && Directory.Exists(Path.Combine(cur, "src", "Mcp")))
                return Path.Combine(cur, "src", "Mcp");
            cur = Directory.GetParent(cur)?.FullName;
        }

        throw new InvalidOperationException("Could not locate src/Mcp directory.");
    }
}
