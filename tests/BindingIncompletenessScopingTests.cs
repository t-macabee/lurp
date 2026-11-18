using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using RoslynDocumentId = Microsoft.CodeAnalysis.DocumentId;

namespace Lurp.Tests;

/// <summary>
///     Unit coverage for the R6 lockstep filter that scopes the incremental
///     binding-incompleteness save to the deletion set. The end-to-end null-path
///     bucket (doc-less <c>extractor_failure</c> aggregates) cannot be reproduced
///     deterministically from source — it requires an extractor stage to throw —
///     so the null-bucket carry-forward guarantee is pinned here at the predicate.
/// </summary>
public sealed class BindingIncompletenessScopingTests : IntegrationTestBase
{
    private static BindingIncompletenessRecord Row(string? documentPath, string reason, int count)
    {
        return new BindingIncompletenessRecord("App", documentPath, reason, count, "v-test");
    }

    [Fact]
    public void ScopeToDocuments_ExcludesNullPathBucket_ForCarryForward()
    {
        // An affected project re-extracted with a document-scoped pass may emit
        // a PARTIAL doc-less aggregate (document_path = null/""). Including it in
        // the scoped save overwrites the copied-forward full count via the
        // ON CONFLICT upsert, undercounting the null bucket. The lockstep policy
        // (TRUST_KERNEL R6, EXCLUDE-AND-CARRY-FORWARD) requires the save to drop
        // null/empty-path records so their carried-forward value survives.
        var scope = new HashSet<string>(StringComparer.Ordinal) { "App/Helper.cs" };
        var records = new[]
        {
            Row("App/Helper.cs", "filtered_external", 3), // in scope — kept
            Row("App/External.cs", "filtered_external", 5), // out of scope — dropped
            Row(null, "extractor_failure", 7), // null bucket — must be dropped (carry-forward)
            Row("", "extractor_failure", 2) // empty bucket — must be dropped (carry-forward)
        };

        var scoped = IncrementalIndexer.ScopeToDocuments(records, r => r.DocumentPath, scope);

        Assert.Single(scoped);
        Assert.Equal("App/Helper.cs", scoped[0].DocumentPath);
        Assert.DoesNotContain(scoped, r => string.IsNullOrEmpty(r.DocumentPath));
    }

    [Fact]
    public async Task ExcludedDocumentRows_AndBucket_SurviveAnIncrementalRunOfTheCaller()
    {
        File.WriteAllText(Path.Combine(TestDir, ".gitignore"), "Secret.cs\n");

        CreateProject("Lib", new Dictionary<string, string>
        {
            ["Secret.cs"] = """
                            namespace Lib;

                            public static class Secret
                            {
                                public static int Value() => 7;
                            }
                            """
        });
        CreateProject("App", new Dictionary<string, string>
        {
            ["Caller.cs"] = """
                            namespace App;

                            public static class Caller
                            {
                                public static int Run() => Lib.Secret.Value() + Lib.GenC.Hidden();
                            }
                            """
        }, projectReferences: ["Lib"]);

        var libCsproj = Path.Combine(TestDir, "src", "Lib", "Lib.csproj");
        var csprojText = File.ReadAllText(libCsproj).Replace(
            "</Project>",
            """
              <ItemGroup>
                <Compile Include="obj/GenC.cs" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(libCsproj, csprojText);

        var libObjDir = Path.Combine(TestDir, "src", "Lib", "obj");
        Directory.CreateDirectory(libObjDir);
        File.WriteAllText(Path.Combine(libObjDir, "GenC.cs"), """
                                                             namespace Lib;

                                                             public static class GenC
                                                             {
                                                                 public static int Hidden() => 3;
                                                             }
                                                             """);
        RestoreSolution();

        await RunFullIndexAsync(DbPath);

        WriteFile("App", "Caller.cs", """
                                      namespace App;

                                      public static class Caller
                                      {
                                          public static int Run() => Lib.Secret.Value() + Lib.GenC.Hidden();
                                          public static int RunAgain() => Lib.GenC.Hidden();
                                      }
                                      """);
        Touch(Path.Combine(TestDir, "src", "App", "Caller.cs"));

        var result = await RunIncrementalIndexWithResultAsync();

        Assert.Equal(0, result.OrphanEdgesDropped.Other);
        Assert.True(result.OrphanEdgesDropped.ExcludedDocument >= 1,
            $"Expected excluded_document >= 1, got {result.OrphanEdgesDropped.FormatDropSummary()}");

        using var store = OpenStore(DbPath);
        var libIncompleteness = store.GetBindingIncompleteness(result.NewSnapshotId, "Lib");
        var excludedRow = Assert.Single(libIncompleteness, r => r.Reason == BindingIncompletenessReason.ExcludedDocument);
        Assert.Equal("src/Lib/obj/GenC.cs", excludedRow.DocumentPath);
        Assert.Equal(1, excludedRow.Count);
    }

    [Fact]
    public async Task ExcludedDocumentSymbols_TwoProjectsWithSameName_CollectsFromBothWithoutThrowing()
    {
        var dirA = Path.Combine(TestDir, "a");
        var dirB = Path.Combine(TestDir, "b");
        var objA = Path.Combine(dirA, "obj");
        var objB = Path.Combine(dirB, "obj");
        Directory.CreateDirectory(objA);
        Directory.CreateDirectory(objB);

        var mainAPath = Path.Combine(dirA, "MainA.cs");
        var genAPath = Path.Combine(objA, "GenA.cs");
        var mainBPath = Path.Combine(dirB, "MainB.cs");
        var genBPath = Path.Combine(objB, "GenB.cs");

        const string mainASource = "namespace A; public class MainA { }";
        const string genASource = "namespace A; public static class GenA { public static int One() => 1; }";
        const string mainBSource = "namespace B; public class MainB { }";
        const string genBSource = "namespace B; public static class GenB { public static int Two() => 2; }";

        File.WriteAllText(mainAPath, mainASource);
        File.WriteAllText(genAPath, genASource);
        File.WriteAllText(mainBPath, mainBSource);
        File.WriteAllText(genBPath, genBSource);

        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator)
            .Where(static p => !string.IsNullOrEmpty(p))
            .Select(static p => MetadataReference.CreateFromFile(p))
            .ToArray();

        using var workspace = new AdhocWorkspace();
        workspace.AddSolution(SolutionInfo.Create(
            SolutionId.CreateNewId(), VersionStamp.Create(), Path.Combine(TestDir, "Test.slnx")));
        var projectAId = ProjectId.CreateNewId();
        var projectBId = ProjectId.CreateNewId();

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectAId, VersionStamp.Create(), "SharedName", "SharedNameA", LanguageNames.CSharp,
                filePath: Path.Combine(dirA, "SharedName.csproj"),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: references))
            .AddDocument(RoslynDocumentId.CreateNewId(projectAId), "MainA.cs", SourceText.From(mainASource, Encoding.UTF8), filePath: mainAPath)
            .AddDocument(RoslynDocumentId.CreateNewId(projectAId), "GenA.cs", SourceText.From(genASource, Encoding.UTF8), filePath: genAPath)
            .AddProject(ProjectInfo.Create(
                projectBId, VersionStamp.Create(), "SharedName", "SharedNameB", LanguageNames.CSharp,
                filePath: Path.Combine(dirB, "SharedName.csproj"),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: references))
            .AddDocument(RoslynDocumentId.CreateNewId(projectBId), "MainB.cs", SourceText.From(mainBSource, Encoding.UTF8), filePath: mainBPath)
            .AddDocument(RoslynDocumentId.CreateNewId(projectBId), "GenB.cs", SourceText.From(genBSource, Encoding.UTF8), filePath: genBPath);

        var workspaceInfo = new WorkspaceInfo(solution, TestDir);

        Assert.Equal(2, workspaceInfo.ExcludedDocuments.Count);
        Assert.Equal(["a/obj/GenA.cs"], workspaceInfo.GetExcludedDocuments(solution.GetProject(projectAId), "SharedName"));
        Assert.Equal(["b/obj/GenB.cs"], workspaceInfo.GetExcludedDocuments(solution.GetProject(projectBId), "SharedName"));

        var excludedIds = await ExcludedDocumentSymbols.CollectAsync(solution, workspaceInfo, CancellationToken.None);

        Assert.Contains(excludedIds, id => id.StartsWith("T:A.GenA|", StringComparison.Ordinal));
        Assert.Contains(excludedIds, id => id.StartsWith("M:A.GenA.One|", StringComparison.Ordinal));
        Assert.Contains(excludedIds, id => id.StartsWith("T:B.GenB|", StringComparison.Ordinal));
        Assert.Contains(excludedIds, id => id.StartsWith("M:B.GenB.Two|", StringComparison.Ordinal));
    }
}