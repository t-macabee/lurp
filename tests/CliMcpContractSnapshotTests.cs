using System.Reflection;
using System.Xml.Linq;
using Lurp.Mcp;
using Lurp.Storage;
using Lurp.Workspace;
using ModelContextProtocol.Server;

namespace Lurp.Tests;

/// <summary>
/// Snapshots the CLI mode + flag inventory and the MCP tool + param surface.
/// Analogous to <c>EveryReadMode_Registry_DeclaresFreshnessFlags</c> and
/// <c>MigrationList_MatchesTheMigrationFiles_AndLatestVersionIsTheHighest</c>: any change to Program.ModeRegistry or to
/// src/Mcp/Tools/*.cs is a visible, deliberate diff in this file rather than
/// a silent shape change. See VERSIONING.md for breaking vs non-breaking rules.
/// </summary>
public sealed class CliMcpContractSnapshotTests
{
    [Fact]
    public void ContractVersion_IsExpected()
    {
        Assert.Equal(9, VersionConstants.CliMcpContractVersion);
    }

    [Fact]
    public void OutputSchemaVersion_IsExpected()
    {
        // D102: the capsule document version. Source: RELEASE_NOTES_3.6.0.md "Versions" table
        // (Output schema v6) and RELEASE_NOTES_3.0.0.md (5 -> 6). It moves only on a breaking
        // change of the capsule document (VERSIONING.md).
        Assert.Equal(8, VersionConstants.OutputSchemaVersion);
    }

    [Fact]
    public void ToolVersion_IsTheCsprojVersion()
    {
        // Oracle: the one <Version> property of src/Lurp.csproj (D105). ToolVersion is derived from
        // the assembly's informational version; this checks the derivation, including the "+<commit>"
        // suffix that the SDK appends in a git checkout.
        var csproj = XDocument.Load(Path.Combine(TestRepoRoot.Find(), "src", "Lurp.csproj"));
        var version = Assert.Single(csproj.Root!.Elements("PropertyGroup").Elements("Version")).Value.Trim();

        Assert.Equal(version, VersionConstants.ToolVersion);
    }

    [Fact]
    public void BuildVersionOutput_PinsToolAndSubsystemVersions()
    {
        var output = Program.BuildVersionOutput();

        var lines = output.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal($"lurp {VersionConstants.ToolVersion}", lines[0]);
        Assert.Contains($"schema v{MigrationRunner.LatestVersion}", lines[1]);
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
            "impact:--symbol=,--direction=,--kinds=,--provenance=,--max-depth=,--limit=,--cursor=,--snapshot=,--output=,--freshness=,--require-fresh,--quiet",
            "index:--solution=,--strategy=,--output-json=,--skip-adapter=,--skip-analyzers,--skip-diff,--verbose,--force",
            "navigate:--file=,--line=,--include-generated,--snapshot=,--freshness=,--require-fresh,--quiet",
            "outline:--document=,--include-generated,--limit=,--cursor=,--snapshot=,--output=,--freshness=,--require-fresh,--quiet",
            "pin-snapshot:--snapshot=,--clear,--json,--output=",
            "retract-annotation:--annotation-id=,--snapshot=",
            "search:--query=,--type=,--kind=,--limit=,--snippet-tokens=,--cursor=,--include-generated,--snapshot=,--output=,--freshness=,--require-fresh,--quiet",
            "serve:--solution=,--enable-write-tools",
            "status:--solution=,--detail=,--json,--output=,--max-mismatches=",
            "timings:--snapshot=,--json",
        };

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void McpToolSurface_SnapshotMatchesExpected()
    {
        var actual = GetMcpSurface()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        // Parameters appear in declaration order; ! marks required inputs.
        // dest= appears for write tools only. The SDK defaults Destructive to true, so the value says nothing on a read-only tool.
        var expected = new[]
        {
            "lurp_context[ro=true,open=false](symbol:string,file:string,line:int,intent:string,content_budget:int,max_hops:int,scope:string,affected_project:string[],include_generated:bool,completeness_detail:bool,tier:string,tier_limit:int,cursor:string,snapshot_id:string)",
            "lurp_dead_candidates[ro=true,open=false](limit:int,cursor:string,snapshot_id:string,project:string,document:string,kind:string,include_public:bool,include_generated:bool,include_tests:bool)",
            "lurp_diagnostics[ro=true,open=false](document:string,project:string,severity:string,id:string,limit:int,cursor:string,include_generated:bool,snapshot_id:string)",
            "lurp_diff[ro=true,open=false](from_snapshot:string!,to_snapshot:string!)",
            "lurp_find_symbol[ro=true,open=false](symbol:string!,include_generated:bool,snapshot_id:string)",
            "lurp_get_annotations[ro=true,open=false](symbol:string,document:string,kind:string,limit:int,cursor:string,snapshot_id:string)",
            "lurp_get_source[ro=true,open=false](document:string!,start_line:int,end_line:int,context_lines:int,snapshot_id:string,outline:bool)",
            "lurp_get_symbol[ro=true,open=false](symbol:string!,view:string,context_lines:int,include_generated:bool,snapshot_id:string)",
            "lurp_grep[ro=true,open=false](query:string!,limit:int,cursor:string,ignore_case:bool,include_generated:bool,snapshot_id:string)",
            "lurp_impact[ro=true,open=false](symbol:string!,direction:string,kinds:string[],provenance:string[],max_depth:int,limit:int,cursor:string,snapshot_id:string)",
            "lurp_index[ro=false,dest=true,open=false](strategy:string,force:bool,operation_id:string,cancel:bool,skip_analyzers:bool)",
            "lurp_navigate[ro=true,open=false](file:string!,line:int!,include_generated:bool,snapshot_id:string)",
            "lurp_outline[ro=true,open=false](document:string!,include_generated:bool,limit:int,cursor:string,snapshot_id:string)",
            "lurp_refresh[ro=true,open=false](ack:string,snapshot_id:string)",
            "lurp_retract_annotation[ro=false,dest=true,open=false](annotation_id:long!,snapshot_id:string)",
            "lurp_search[ro=true,open=false](query:string!,type:string,kind:string,limit:int,snippet_tokens:int,cursor:string,include_generated:bool,snapshot_id:string)",
            "lurp_status[ro=true,open=false](snapshot_id:string,detail:object,sections:string,max_documents:int,max_mismatches:int,documents:string[],full:bool,projects:string[],max_bytes:int)",
            "lurp_timings[ro=true,open=false](snapshot_id:string)",
        };

        Assert.Equal(expected, actual);
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
                var parameters = method.GetParameters()
                    .Where(p => p.ParameterType != typeof(CancellationToken))
                    .Select(p => $"{p.Name}:{ParameterTypeName(p.ParameterType)}{(p.IsDefined(typeof(McpRequiredAttribute)) ? "!" : "")}");
                var annotations = (attr.ReadOnly
                    ? $"ro=true,open={attr.OpenWorld}"
                    : $"ro=false,dest={attr.Destructive},open={attr.OpenWorld}").ToLowerInvariant();
                yield return $"{attr.Name}[{annotations}]({string.Join(",", parameters)})";
            }
        }
    }

    private static string ParameterTypeName(Type type)
    {
        if (type.IsArray)
            return ParameterTypeName(type.GetElementType()!) + "[]";

        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return "string";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(object)) return "object";
        return type.FullName ?? type.Name;
    }
}
