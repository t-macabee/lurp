using System.ComponentModel;
using System.Reflection;
using Lurp.Adapters;
using Lurp.Mcp;
using Lurp.Storage;
using Lurp.Tests.Docs;
using Lurp.Workspace;

namespace Lurp.Tests;

public sealed class DocRenderersTests
{
    [Fact]
    public void RenderFlagTable_FormatsEachRowKind()
    {
        var specs = new[]
        {
            new FlagSpec("--bare", "Bare flag."),
            new FlagSpec("--hint=", "Hint.", "path"),
            new FlagSpec("--list=", "List.", AllowedValues: ["summary", "json"]),
            new FlagSpec("--req=", "Req.", "path", Requirement: FlagRequirement.Required, EnvironmentVariable: "LURP_REQ"),
            new FlagSpec("--rep=", "Rep.", "name", Repeatable: true),
            new FlagSpec("--pipe", "a | b"),
        };

        var expected =
            "\n| Argument | Required | Description |\n|---|---|---|\n" +
            "| `--bare` | No | Bare flag. |\n" +
            "| `--hint=<path>` | No | Hint. |\n" +
            "| `--list=<summary\\|json>` | No | List. |\n" +
            "| `--req=<path>` | Yes, or `LURP_REQ` | Req. |\n" +
            "| `--rep=<name>` | No | Rep. May be repeated. |\n" +
            "| `--pipe` | No | a \\| b |\n";

        Assert.Equal(expected, DocRenderers.RenderFlagTable(specs));
    }

    [Fact]
    public void RenderModeFlags_LeavesOutTheSharedReadOptions()
    {
        var entry = new Program.ModeRegistryEntry(
            "test-mode",
            "test",
            [FlagCatalog.Query, FlagCatalog.Output, FlagCatalog.Quiet, FlagCatalog.Freshness, FlagCatalog.RequireFresh],
            _ => Task.CompletedTask);

        var text = DocRenderers.RenderModeFlags(entry);

        Assert.Equal(DocRenderers.RenderFlagTable([FlagCatalog.Query]), text);
        Assert.DoesNotContain("`--output=", text);
        Assert.DoesNotContain("`--quiet`", text);
        Assert.DoesNotContain("`--freshness=", text);
        Assert.DoesNotContain("`--require-fresh`", text);

        var changed = new Program.ModeRegistryEntry(
            "test-mode",
            "test",
            [FlagCatalog.Query, FlagCatalog.Output with { AllowedValues = ["summary", "json"] }, FlagCatalog.Quiet, FlagCatalog.Freshness, FlagCatalog.RequireFresh],
            _ => Task.CompletedTask);

        Assert.Contains("| `--output=<summary\\|json>` | No |", DocRenderers.RenderModeFlags(changed));
    }

    [Fact]
    public void RenderReadOptions_ListsTheFourOptions_InOrder()
    {
        var text = DocRenderers.RenderReadOptions();

        var rows = text.Split('\n').Where(l => l.StartsWith("| `", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, rows.Count);

        var output = text.IndexOf("`--output=", StringComparison.Ordinal);
        var quiet = text.IndexOf("`--quiet`", StringComparison.Ordinal);
        var freshness = text.IndexOf("`--freshness=", StringComparison.Ordinal);
        var requireFresh = text.IndexOf("`--require-fresh`", StringComparison.Ordinal);
        Assert.True(output >= 0 && output < quiet && quiet < freshness && freshness < requireFresh);
    }

    [Fact]
    public void AllowedValues_AreRegisteredInTheCodeConstants()
    {
        var deadCandidates = Program.FindMode("dead-candidates")!;
        var kind = deadCandidates.Specs.Single(spec => spec.Name == "--kind=");
        Assert.Equal(DeadCandidateLiveness.CandidateKinds, kind.AllowedValues);

        var context = Program.FindMode("context")!;
        var tier = context.Specs.Single(spec => spec.Name == "--tier=");
        Assert.Equal(ContextAssembler.TierNames, tier.AllowedValues);

        Assert.Equal(IndexRunner.Strategies, FlagCatalog.Strategy.AllowedValues);
        Assert.Equal(AdapterRegistry.Names, FlagCatalog.SkipAdapter.AllowedValues);
    }

    [Fact]
    public void RenderMcpToolTable_HasEveryToolInCatalogOrder()
    {
        var table = DocRenderers.RenderMcpToolTable();

        Assert.StartsWith(
            "\n| Tool | Access | Parameters | Description |\n|---|---|---|---|\n| `lurp_context` | read |",
            table);

        var rows = table.Split('\n').Where(line => line.StartsWith("| `", StringComparison.Ordinal)).ToList();
        Assert.Equal(18, rows.Count);
    }

    [Fact]
    public void RenderMcpToolTable_MarksWriteToolsAndRendersParameters()
    {
        var table = DocRenderers.RenderMcpToolTable();

        Assert.Contains("| `lurp_index` | write (needs `--enable-write-tools`) |", table);
        Assert.Contains("Both from_snapshot and to_snapshot are required.", table);
        Assert.Contains("(bool, default `false`)", table);
        Assert.Contains("`affected_project` (string[])", table);
    }

    [Fact]
    public void RenderMcpWriteTools_PutsEachNameInBackticks()
    {
        Assert.Equal("`lurp_index` and `lurp_retract_annotation`", DocRenderers.RenderMcpWriteTools());
    }

    [Fact]
    public void EveryMcpLimit_HasADescription()
    {
        var fields = typeof(McpLimits).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .ToList();

        Assert.Equal(7, fields.Count);
        Assert.All(fields, field =>
            Assert.False(string.IsNullOrWhiteSpace(field.GetCustomAttribute<DescriptionAttribute>()?.Description), field.Name));
    }

    [Fact]
    public void RenderMcpLimits_HasOneRowPerConstant_InDeclarationOrder()
    {
        var expected =
            "\n| Parameter | Maximum |\n|---|---|\n" +
            "| `limit` and `tier_limit` on every tool except `lurp_dead_candidates`; `max_documents`, `max_mismatches` and the length of `documents` on `lurp_status` | 500 |\n" +
            "| `limit` on `lurp_dead_candidates` | 200 |\n" +
            "| `content_budget` on `lurp_context` (estimated tokens) | 200000 |\n" +
            "| `max_depth` on `lurp_impact` | 20 |\n" +
            "| `max_hops` on `lurp_context` | 20 |\n" +
            "| `snippet_tokens` on `lurp_search` | 2000 |\n" +
            "| `max_bytes` on `lurp_status` (UTF-8 bytes) | 400000 |\n";

        Assert.Equal(expected, DocRenderers.RenderMcpLimits());
    }

    [Fact]
    public void RenderMcpToolAccessTable_ListsEveryTool_WithItsRegistrationAndAnnotations()
    {
        var text = DocRenderers.RenderMcpToolAccessTable();
        var rows = text.Split('\n').Where(line => line.StartsWith("| `", StringComparison.Ordinal)).ToList();

        Assert.StartsWith("\n| Tool | Registered | Effect (MCP annotations) |\n|---|---|---|\n", text);
        Assert.Equal(McpServeHandler.ReadToolTypes.Length + McpServeHandler.WriteToolTypes.Length, rows.Count);
        Assert.Contains("| `lurp_context` | always | read-only |", rows);
        Assert.Contains("| `lurp_index` | only with `--enable-write-tools` | writes, destructive |", rows);
        Assert.Contains("| `lurp_retract_annotation` | only with `--enable-write-tools` | writes, destructive |", rows);
    }
}
