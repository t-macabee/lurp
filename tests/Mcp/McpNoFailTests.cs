using System.Text.RegularExpressions;

namespace Lurp.Tests.Mcp;

public sealed class McpNoFailTests
{
    [Fact]
    public void McpSource_ContainsZeroDirectFailCallSites()
    {
        var mcpDir = FindMcpDirectory();
        var files = Directory.GetFiles(mcpDir, "*.cs", SearchOption.AllDirectories);
        var failPattern = new Regex(@"HandlerBootstrap\.Fail\s*\(", RegexOptions.Compiled);

        var hits = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match m in failPattern.Matches(text))
            {
                var line = text[..m.Index].Count(c => c == '\n') + 1;
                hits.Add($"{Path.GetFileName(file)}:{line}: {m.Value}");
            }
        }

        Assert.Empty(hits);
    }

    private static string FindMcpDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "src", "Mcp");
            if (Directory.Exists(candidate))
                return candidate;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

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
