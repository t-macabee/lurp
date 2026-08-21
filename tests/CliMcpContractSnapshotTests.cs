using System.Reflection;
using Lurp.Workspace;
using ModelContextProtocol.Server;

namespace Lurp.Tests;

/// <summary>
/// Snapshots the CLI mode + flag inventory and the MCP tool + param surface.
/// Analogous to <c>EveryReadMode_Registry_DeclaresFreshnessFlags</c> and
/// <c>MigrationList_CountIs28</c>: any change to Program.ModeRegistry or to
/// src/Mcp/Tools/*.cs is a visible, deliberate diff in this file rather than
/// a silent shape change. See VERSIONING.md for breaking vs non-breaking rules.
/// </summary>
public sealed class CliMcpContractSnapshotTests
{
    [Fact]
    public void ContractVersion_IsExpected()
    {
        Assert.Equal(1, VersionConstants.CliMcpContractVersion);
    }

    [Fact]
    public void ToolVersion_MatchesAssemblyPackageVersion()
    {
        // The csproj <Version> is the published NuGet/dotnet-tool version and drives
        // the Lurp assembly version; ToolVersion is what `lurp --version` prints and
        // what snapshot manifests stamp. The two must never disagree again — bump
        // both together (or this test fails).
        var assemblyVersion = typeof(VersionConstants).Assembly.GetName().Version!;
        Assert.Equal(VersionConstants.ToolVersion,
            $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}");
    }

    [Fact]
    public void BuildVersionOutput_PinsToolAndSubsystemVersions()
    {
        var output = Program.BuildVersionOutput();

        var lines = output.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal($"lurp {VersionConstants.ToolVersion}", lines[0]);
        Assert.Contains($"schema v{VersionConstants.DatabaseSchemaVersion}", lines[1]);
        Assert.Contains($"extractor v{VersionConstants.ExtractorVersion}", lines[1]);
        Assert.Contains($"cli/mcp contract v{VersionConstants.CliMcpContractVersion}", lines[1]);
        Assert.Contains($"output schema v{VersionConstants.OutputSchemaVersion}", lines[1]);
    }

    [Fact]
    public void CliModeRegistry_SnapshotMatchesExpected()
    {
        // Actual surface derived from the single source of truth Program.ModeRegistry.
        // Format: "mode:flag,flag,flag" where flags are in declared order (presentation order).
        var actual = Program.ModeRegistry
            .Select(e => $"{e.Name}:{string.Join(",", e.Flags)}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        // Expected snapshot — update deliberately when adding/removing modes or flags.
        // Sorted alphabetically by mode name for stable diffs. Flags remain in the
        // order declared in src/Program.cs (not sorted) so the string is a faithful
        // record of the registry entry.
        var expected = new[]
        {
            "annotate:--symbol=,--annotation-kind=,--value=,--snapshot=",
            "context:--symbol=,--file=,--line=,--intent=,--content-budget=,--max-hops=,--scope=,--affected-project=,--tier=,--tier-limit=,--cursor=,--completeness-detail,--include-generated,--snapshot=,--output=,--freshness=,--require-fresh,--quiet",
            "dead-candidates:--project=,--document=,--kind=,--limit=,--cursor=,--snapshot=,--output=,--freshness=,--require-fresh,--quiet,--include-public,--include-generated,--include-tests",
            "diagnostics:--document=,--project=,--severity=,--id=,--limit=,--cursor=,--include-generated,--snapshot=,--output=,--freshness=,--require-fresh,--quiet",
            "diff:--from-snapshot=,--to-snapshot=",
            "find-symbol:--symbol=,--include-generated,--snapshot=,--output=,--freshness=,--require-fresh,--quiet",
            "get-annotations:--symbol=,--document=,--kind=,--limit=,--cursor=,--snapshot=,--output=,--freshness=,--require-fresh,--quiet",
            "get-source:--document=,--start-line=,--end-line=,--context-lines=,--snapshot=,--freshness=,--require-fresh,--quiet",
            "get-symbol:--symbol=,--view=,--context-lines=,--include-generated,--snapshot=,--freshness=,--require-fresh,--quiet",
            "grep:--query=,--limit=,--cursor=,--ignore-case,--include-generated,--snapshot=,--output=,--freshness=,--require-fresh,--quiet",
            "impact:--symbol=,--direction=,--kinds=,--provenance=,--max-depth=,--max-paths=,--cursor=,--snapshot=,--output=,--freshness=,--require-fresh,--quiet",
            "index:--solution=,--strategy=,--output-json=,--skip-adapter=,--skip-diff,--verbose,--force",
            "navigate:--file=,--line=,--include-generated,--snapshot=,--freshness=,--require-fresh,--quiet",
            "outline:--document=,--include-generated,--limit=,--cursor=,--snapshot=,--output=,--freshness=,--require-fresh,--quiet",
            "pin-snapshot:--snapshot=,--clear,--json,--output=",
            "retract-annotation:--annotation-id=,--snapshot=",
            "search:--query=,--type=,--kind=,--limit=,--snippet-tokens=,--cursor=,--include-generated,--snapshot=,--output=,--freshness=,--require-fresh,--quiet",
            "serve:--solution=",
            "status:--solution=,--detail=,--json,--output=",
            "timings:--snapshot=,--json",
        };

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CliModeRegistry_CountIs20()
    {
        Assert.Equal(20, Program.ModeRegistry.Length);
    }

    [Fact]
    public void McpToolSurface_SnapshotMatchesExpected()
    {
        var actual = GetMcpSurface()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        // Expected snapshot — update deliberately when adding/removing tools or params.
        // Format: "tool_name(param1,param2,...)" where params are in declared order
        // (method signature order) and names are snake_case as exposed to callers.
        // Tool names are MCP-facing (McpServerTool.Name), not C# method names.
        var expected = new[]
        {
            "lurp_context(symbol,file,line,intent,content_budget,max_hops,scope,affected_project,include_generated,completeness_detail,tier,tier_limit,cursor,snapshot_id)",
            "lurp_dead_candidates(limit,cursor,snapshot_id,project,document,kind,include_public,include_generated,include_tests)",
            "lurp_diagnostics(document,project,severity,id,limit,cursor,include_generated,snapshot_id)",
            "lurp_diff(from_snapshot,to_snapshot)",
            "lurp_find_symbol(symbol,include_generated,snapshot_id)",
            "lurp_get_annotations(symbol,document,kind,limit,cursor,snapshot_id)",
            "lurp_get_source(document,start_line,end_line,context_lines,snapshot_id,outline)",
            "lurp_get_symbol(symbol,view,context_lines,include_generated,snapshot_id)",
            "lurp_grep(query,limit,cursor,ignore_case,include_generated,snapshot_id)",
            "lurp_impact(symbol,direction,kinds,provenance,max_depth,max_paths,cursor,snapshot_id)",
            "lurp_index(solution,strategy,force,operation_id,cancel)",
            "lurp_navigate(file,line,include_generated,snapshot_id)",
            "lurp_outline(document,include_generated,limit,cursor,snapshot_id)",
            "lurp_refresh(ack,snapshot_id)",
            "lurp_retract_annotation(annotation_id,snapshot_id)",
            "lurp_search(query,type,kind,limit,snippet_tokens,cursor,include_generated,snapshot_id)",
            "lurp_status(snapshot_id,detail,sections,max_documents,max_mismatches,documents)",
            "lurp_timings(snapshot_id)",
        };

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void McpToolSurface_CountIs18()
    {
        Assert.Equal(18, GetMcpSurface().Count());
    }

    [Fact]
    public void McpToolSurface_AllNamesAreLurpPrefixed()
    {
        foreach (var entry in GetMcpSurface())
        {
            var name = entry[..entry.IndexOf('(')];
            Assert.StartsWith("lurp_", name);
        }
    }

    private static IEnumerable<string> GetMcpSurface()
    {
        var asm = typeof(Program).Assembly;
        var toolTypes = asm.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null);

        foreach (var type in toolTypes)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null);

            foreach (var method in methods)
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>()!;
                var paramNames = method.GetParameters()
                    .Where(p => p.ParameterType != typeof(System.Threading.CancellationToken))
                    .Select(p => p.Name!)
                    .ToArray();
                yield return $"{attr.Name}({string.Join(",", paramNames)})";
            }
        }
    }
}
