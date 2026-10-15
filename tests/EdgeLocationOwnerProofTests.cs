using Lurp.Shared;
using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lurp.Tests;

/// <summary>
///     B98 proof: the edge-location owners must give one answer for one location.
/// </summary>
public sealed class EdgeLocationOwnerProofTests : IDisposable
{
    private readonly string _gitRoot = Path.Combine(Path.GetTempPath(), $"lurp-loc-{Guid.NewGuid():N}");

    public EdgeLocationOwnerProofTests()
    {
        Directory.CreateDirectory(_gitRoot);
    }

    public void Dispose()
    {
        TestDatabase.DeleteDirectory(_gitRoot);
    }

    [Theory]
    [InlineData("Gen/Hint.g.cs")]
    [InlineData("Some.Generator/Some.Generator.Gen/Hint.g.cs")]
    public void UnrootedTreePath_ReflectionContextAndResolver_GiveTheSamePath(string treePath)
    {
        var tree = CSharpSyntaxTree.ParseText("class C { }", path: treePath);
        var compilation = CSharpCompilation.Create("P", [tree]);
        var location = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single().GetLocation();

        var context = new ReflectionExtractionContext(compilation, "snap", _gitRoot);
        var resolver = new EdgeLocationResolver([], [], _gitRoot);

        Assert.Equal(resolver.Resolve(location).path, context.GetLocationInfo(location).path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrimaryDeclaration_PrefersTheHandWrittenPart(bool designerFirst)
    {
        var handWritten = CSharpSyntaxTree.ParseText("partial class Initial : System.Object { }", path: Path.Combine(_gitRoot, "Migrations", "Initial.cs"));
        var designer = CSharpSyntaxTree.ParseText("partial class Initial { }", path: Path.Combine(_gitRoot, "Migrations", "Initial.Designer.cs"));
        SyntaxTree[] trees = designerFirst ? [designer, handWritten] : [handWritten, designer];
        var compilation = CSharpCompilation.Create("P", trees);

        var primary = EdgeLocationResolver.PrimaryDeclaration(compilation.GetTypeByMetadataName("Initial")!);

        Assert.Equal(handWritten.FilePath, primary!.SyntaxTree.FilePath);
    }

    [Fact]
    public void PrimaryDeclaration_AllPartsGenerated_UsesOrdinalPathOrder()
    {
        var b = CSharpSyntaxTree.ParseText("partial class G { }", path: "Gen/B.g.cs");
        var a = CSharpSyntaxTree.ParseText("partial class G { }", path: "Gen/A.g.cs");
        var compilation = CSharpCompilation.Create("P", [b, a]);

        var primary = EdgeLocationResolver.PrimaryDeclaration(compilation.GetTypeByMetadataName("G")!);

        Assert.Equal(a.FilePath, primary!.SyntaxTree.FilePath);
    }
}

public sealed class EdgeLocationGeneratorProofTests : IntegrationTestBase
{
    [SkippableFact]
    public async Task SourceGeneratedTree_EveryEdgeSourcePath_IsAStoredDocumentPath()
    {
        Skip.If(!Microsoft.Build.Locator.MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("Gen", new Dictionary<string, string>
        {
            ["Model.cs"] = """
                           using System.Text.Json.Serialization;

                           namespace Gen;

                           public class Order
                           {
                               public int Id { get; set; }
                               public string? Name { get; set; }
                           }

                           [JsonSerializable(typeof(Order))]
                           public partial class OrderContext : JsonSerializerContext { }
                           """
        });
        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT extractor_version, kind, source_document_path, COUNT(*)
            FROM edges
            WHERE snapshot_id = @snapshotId
              AND source_document_path IS NOT NULL
              AND source_document_path NOT IN (SELECT relative_path FROM documents)
            GROUP BY extractor_version, kind, source_document_path
            ORDER BY extractor_version, kind, source_document_path;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        var rows = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                rows.Add($"{reader.GetString(0)} {reader.GetString(1)} {reader.GetString(2)} x{reader.GetInt64(3)}");

        Assert.True(rows.Count == 0, "Edge source paths that are not stored documents:\n" + string.Join("\n", rows));
    }
}
