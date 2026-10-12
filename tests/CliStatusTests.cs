using Lurp.Handlers;
using System.Text.Json;

namespace Lurp.Tests;

/// <summary>
///     CLI coverage for <c>--mode=status</c>: the registry accepts
///     <c>--max-mismatches=</c> (and rejects the removed <c>--max-documents=</c>),
///     and the documented cap is enforced on the freshness report.
/// </summary>
public sealed class CliStatusTests : IntegrationTestBase
{
    [Fact]
    public void StatusRegistry_AcceptsMaxMismatches_RejectsMaxDocuments()
    {
        var entry = Program.FindMode("status")!;

        CliFlagValidation.Validate(entry, ["--output-dir=.", "--max-mismatches=5"]);

        var ex = Assert.Throws<CliExitException>(() =>
            CliFlagValidation.Validate(entry, ["--output-dir=.", "--max-documents=5"]));
        Assert.Contains("unknown flag '--max-documents='", ex.Message);
    }

    [Fact]
    public async Task Status_MaxMismatches_CapsMismatchesAndSetsTruncated()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["One.cs"] = "namespace App { public class One { public void M() {} } }",
            ["Two.cs"] = "namespace App { public class Two { public void M() {} } }",
            ["Three.cs"] = "namespace App { public class Three { public void M() {} } }"
        });
        await RunFullIndexAsync(DbPath);

        WriteFile("App", "One.cs", "namespace App { public class One { public void M(int x) {} } }");
        WriteFile("App", "Two.cs", "namespace App { public class Two { public void M(int x) {} } }");
        WriteFile("App", "Three.cs", "namespace App { public class Three { public void M(int x) {} } }");

        var result = await RunStatusCapturedAsync([
            $"--output-dir={TestDir}", $"--solution={SolutionPath}", "--json", "--max-mismatches=1"
        ]);

        Assert.Null(result.Failure);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.False(doc.RootElement.GetProperty("is_fresh").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("mismatches").GetArrayLength());
        Assert.True(doc.RootElement.GetProperty("mismatches_truncated").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("mismatches_total").GetInt32() > 1);
    }

    [Fact]
    public async Task Status_UnknownDetail_FailsBeforeReadingDatabase()
    {
        var result = await RunStatusCapturedAsync([$"--output-dir={TestDir}", "--json", "--detail=bogus"]);

        Assert.NotNull(result.Failure);
        Assert.Contains("--detail", result.Failure!.Message);
        Assert.Contains("documents, references, completeness or all", result.Failure.Message);
    }

    [Fact]
    public async Task Status_DetailUnion_RestoresDocumentsAndReferences()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["One.cs"] = "namespace App { public class One { public void M() {} } }"
        });
        await RunFullIndexAsync(DbPath);

        var result = await RunStatusCapturedAsync([
            $"--output-dir={TestDir}", $"--solution={SolutionPath}", "--json", "--detail=documents,references"
        ]);

        Assert.Null(result.Failure);
        using var doc = JsonDocument.Parse(result.Stdout);
        var manifest = doc.RootElement.GetProperty("manifest");
        Assert.True(manifest.TryGetProperty("document_versions", out var docVers));
        Assert.Equal(JsonValueKind.Object, docVers.ValueKind);
        Assert.True(manifest.TryGetProperty("metadata_reference_identities", out var ids));
        Assert.Equal(JsonValueKind.Object, ids.ValueKind);
    }

    private static async Task<(string Stdout, CliExitException? Failure)> RunStatusCapturedAsync(string[] args)
    {
        var stdout = new StringWriter();
        var originalOut = Console.Out;
        CliExitException? failure = null;
        try
        {
            Console.SetOut(stdout);
            await StatusHandler.Run(args);
        }
        catch (CliExitException ex)
        {
            failure = ex;
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return (stdout.ToString(), failure);
    }
}
