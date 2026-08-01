using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lurp.Storage.Tests;

/// <summary>
/// Covers the load gate and the empty-versus-unresolved distinction: a capsule must
/// never report an unobservable region as a proved absence of relationships.
/// </summary>
public sealed class WorkspaceLoadGateTests
{
    private const string Source = """
        namespace App
        {
            public class TokenService
            {
                public string CreateToken() => "token";
            }
        }
        """;

    [Fact]
    public void Classify_CompilationWithoutReferences_IsBlind()
    {
        // No metadata references at all: this is the state MSBuildWorkspace silently
        // produces when project evaluation fails, and Roslyn reports CS0518 throughout.
        var compilation = CSharpCompilation.Create(
            "App",
            [CSharpSyntaxTree.ParseText(Source)],
            references: []);

        Assert.Equal(CompilationReadability.Blind, WorkspaceLoadGate.Classify(compilation));
    }

    [Fact]
    public void Classify_CompilationWithCorlib_IsReadable()
    {
        var compilation = CSharpCompilation.Create(
            "App",
            [CSharpSyntaxTree.ParseText(Source)],
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        Assert.Equal(CompilationReadability.Readable, WorkspaceLoadGate.Classify(compilation));
    }

    [Fact]
    public void DescribeBlindProject_MarksEveryDocument()
    {
        var gitRoot = Path.Combine(Path.GetTempPath(), "lurp-gate-test");
        var treePath = Path.Combine(gitRoot, "App", "TokenService.cs");
        var compilation = CSharpCompilation.Create(
            "App",
            [CSharpSyntaxTree.ParseText(Source, path: treePath)],
            references: []);

        var records = WorkspaceLoadGate.DescribeBlindProject(compilation, "App", gitRoot);

        var record = Assert.Single(records);
        Assert.Equal("App", record.ProjectName);
        Assert.Equal(BindingIncompletenessReason.ProjectUnreadable, record.Reason);
        Assert.NotNull(record.DocumentPath);
        Assert.Contains("TokenService.cs", record.DocumentPath);
    }

    [Fact]
    public void DescribeRemediation_NamesTheBuildFaultNotTheSource()
    {
        var message = WorkspaceLoadGate.DescribeRemediation(["API", "Domain"]);

        Assert.Contains("API", message);
        Assert.Contains("Domain", message);
        Assert.Contains("dotnet restore", message);
        // The operator must not be sent hunting through their own source for this.
        Assert.Contains("not a source defect", message);
    }

    [Fact]
    public void EmptyTier_WithLostBindingsOverAnchor_ReportsUnresolvedNotEmpty()
    {
        var capsule = Capsule();
        var tiers = new IContextTierBuilder[] { new Tier("directCallers", []) };

        ContextBudgeter.Apply(capsule, tiers, budget: 100, runningTotal: 0, anchorBindingIsIncomplete: true);

        var entry = Assert.Single(capsule.OmittedTiers);
        Assert.Equal("directCallers", entry.Category);
        Assert.Equal("unresolved", entry.Reason);
    }

    [Fact]
    public void EmptyTier_WithCleanBindings_StillReportsEmpty()
    {
        var capsule = Capsule();
        var tiers = new IContextTierBuilder[] { new Tier("directCallers", []) };

        ContextBudgeter.Apply(capsule, tiers, budget: 100, runningTotal: 0, anchorBindingIsIncomplete: false);

        var entry = Assert.Single(capsule.OmittedTiers);
        Assert.Equal("empty", entry.Reason);
    }

    [Fact]
    public void AnchorRegionHasLostBindings_TrueOnlyWhenAnchorDocumentIsAffected()
    {
        var anchor = AnchorAt("API/Services/TokenService.cs");

        var elsewhere = Record("API/Controllers/AccountController.cs", BindingIncompletenessReason.CompilerError);
        var onAnchor = Record("API/Services/TokenService.cs", BindingIncompletenessReason.CompilerError);

        Assert.False(ContextAssembler.AnchorRegionHasLostBindings(anchor, [elsewhere]));
        Assert.True(ContextAssembler.AnchorRegionHasLostBindings(anchor, [elsewhere, onAnchor]));
    }

    [Fact]
    public void AnchorRegionHasLostBindings_IgnoresFilteredExternal()
    {
        // A filtered external target is a resolved binding whose target is knowably
        // outside the snapshot. That is an explained absence, not an unobservable one,
        // so it must not downgrade a proved "empty" into "unresolved".
        var anchor = AnchorAt("API/Services/TokenService.cs");
        var filtered = Record("API/Services/TokenService.cs", BindingIncompletenessReason.FilteredExternal);

        Assert.False(ContextAssembler.AnchorRegionHasLostBindings(anchor, [filtered]));
    }

    [Fact]
    public void AnchorRegionHasLostBindings_TreatsUnreadableProjectAsUnobservable()
    {
        var anchor = AnchorAt("API/Services/TokenService.cs");
        var unreadable = Record("API/Services/TokenService.cs", BindingIncompletenessReason.ProjectUnreadable);

        Assert.True(ContextAssembler.AnchorRegionHasLostBindings(anchor, [unreadable]));
    }

    private static BindingIncompletenessRecord Record(string documentPath, string reason)
        => new("API", documentPath, reason, Count: 1, ExtractorVersion: "test");

    private static CapsuleAnchor AnchorAt(string documentPath)
        => new("anchor", "Anchor", "Type", "")
        {
            Locations = [new DeclarationLocation(documentPath, 1, 0, 10, 0, IsGenerated: false)],
        };

    private static ContextCapsule Capsule()
        => new(new CapsuleAnchor("anchor", "Anchor", "Type", ""));

    private sealed class Tier(string name, List<CapsuleItem> items) : IContextTierBuilder
    {
        public string Name => name;
        public List<CapsuleItem> Build() => items;
    }
}
