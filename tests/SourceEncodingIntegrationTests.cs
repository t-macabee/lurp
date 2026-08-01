using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lurp.Queries;
using Lurp.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lurp.Storage.Tests;

public sealed class SourceEncodingIntegrationTests : IDisposable
{
    private string? _testDir;

    public void Dispose()
    {
        if (_testDir != null && Directory.Exists(_testDir))
        {
            try
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(_testDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private const string WidgetContent =
        "namespace Library;\n" +
        "\n" +
        "public class Widget\n" +
        "{\n" +
        "    public string Name { get; set; } = \"\";\n" +
        "\n" +
        "    public string GetLabel() => Name;\n" +
        "}\n";

    private (string DbPath, string SolutionPath, string OutputDir) SetupEncodedFixture(byte[] encodedBytes)
    {
        _testDir = Path.Combine(
            Path.GetTempPath(),
            $"lurp_enc_test_{Guid.NewGuid():N}");

        Directory.CreateDirectory(_testDir);
        var dbPath = Path.Combine(_testDir, "index.db");

        var solutionPath = IntegrationHarness.CopyFixtureToTemp(_testDir);

        var widgetPath = Path.Combine(_testDir, "Library", "Widget.cs");
        File.WriteAllBytes(widgetPath, encodedBytes);

        RunGitCommand(_testDir, "init");
        RunGitCommand(_testDir, "config user.email test@test.com");
        RunGitCommand(_testDir, "config user.name test");
        RunGitCommand(_testDir, "add -A");
        RunGitCommand(_testDir, "commit -m init");

        RunDotNetBuild(solutionPath);

        return (dbPath, solutionPath, _testDir);
    }

    [SkippableFact]
    public async Task FullIndex_Utf8BomSource_NormalizesBytesAndPreservesAllViews()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system.");

        var contentBytes = Encoding.UTF8.GetBytes(WidgetContent);
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var withBom = new byte[bom.Length + contentBytes.Length];
        Buffer.BlockCopy(bom, 0, withBom, 0, bom.Length);
        Buffer.BlockCopy(contentBytes, 0, withBom, bom.Length, contentBytes.Length);

        var (dbPath, solutionPath, outputDir) = SetupEncodedFixture(withBom);
        var snapshotId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        var expectedBytes = Encoding.UTF8.GetBytes(WidgetContent);

        var storedBytes = QueryContentBytes(conn, snapshotId, "Library/Widget.cs");
        Assert.NotNull(storedBytes);
        Assert.Equal(expectedBytes, storedBytes);
        Assert.False(storedBytes.Length >= 3 && storedBytes[0] == 0xEF && storedBytes[1] == 0xBB && storedBytes[2] == 0xBF,
            "Stored bytes should not contain a UTF-8 BOM");

        var encoding = QueryEncoding(conn, snapshotId, "Library/Widget.cs");
        Assert.Equal("utf-8", encoding);

        using var store = IntegrationHarness.OpenReadStore(dbPath);
        var queries = new FastTravelQueries(store);

        var source = queries.GetDocument("Library/Widget.cs", snapshotId);
        Assert.NotNull(source);
        Assert.Equal(WidgetContent, source);

        var sourceBytes = System.Text.Encoding.UTF8.GetBytes(source!);
        var firstBytes = sourceBytes.Length >= 3
            ? $"{sourceBytes[0]:X2} {sourceBytes[1]:X2} {sourceBytes[2]:X2}"
            : "too-short";
        Assert.False(sourceBytes.Length >= 3 && sourceBytes[0] == 0xEF && sourceBytes[1] == 0xBB && sourceBytes[2] == 0xBF,
            $"Source text should not start with BOM character. First bytes: {firstBytes}, length={sourceBytes.Length}");

        var symbolIds = QuerySymbolIds(conn, snapshotId, "Library/Widget.cs");
        Assert.NotEmpty(symbolIds);

        var widgetClassId = symbolIds.First(id => id.StartsWith("T:", StringComparison.Ordinal));
        var getLabelId = symbolIds.First(id => id.StartsWith("M:", StringComparison.Ordinal) && id.Contains("GetLabel"));

        var widgetDecl = store.GetSymbolSource(widgetClassId, snapshotId, ViewKind.Declaration);
        Assert.NotNull(widgetDecl);
        Assert.Contains("GetLabel", widgetDecl);

        var nameView = store.GetSymbolSource(widgetClassId, snapshotId, ViewKind.Name);
        Assert.NotNull(nameView);
        Assert.Equal("Widget", nameView);

        var target = queries.Navigate("Library/Widget.cs", 4, snapshotId);
        Assert.NotNull(target);
        Assert.Equal("Library/Widget.cs", target.DocumentPath);

        var sourceResults = store.SearchSource("GetLabel", snapshotId);
        Assert.Contains(sourceResults, r => r.DocumentPath == "Library/Widget.cs");
    }

    [SkippableFact]
    public async Task FullIndex_Utf16LeSource_NormalizesBytesAndPreservesAllViews()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system.");

        var unicode = Encoding.Unicode;
        var preamble = unicode.GetPreamble();
        var contentBytes = unicode.GetBytes(WidgetContent);
        var encoded = new byte[preamble.Length + contentBytes.Length];
        Buffer.BlockCopy(preamble, 0, encoded, 0, preamble.Length);
        Buffer.BlockCopy(contentBytes, 0, encoded, preamble.Length, contentBytes.Length);

        var (dbPath, solutionPath, outputDir) = SetupEncodedFixture(encoded);
        var snapshotId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        var expectedBytes = Encoding.UTF8.GetBytes(WidgetContent);

        var storedBytes = QueryContentBytes(conn, snapshotId, "Library/Widget.cs");
        Assert.NotNull(storedBytes);
        Assert.Equal(expectedBytes, storedBytes);

        var encoding = QueryEncoding(conn, snapshotId, "Library/Widget.cs");
        Assert.Equal("utf-8", encoding);

        using var store = IntegrationHarness.OpenReadStore(dbPath);
        var queries = new FastTravelQueries(store);

        var source = queries.GetDocument("Library/Widget.cs", snapshotId);
        Assert.NotNull(source);
        Assert.Equal(WidgetContent, source);

        var symbolIds = QuerySymbolIds(conn, snapshotId, "Library/Widget.cs");
        Assert.NotEmpty(symbolIds);

        var widgetClassId = symbolIds.First(id => id.StartsWith("T:", StringComparison.Ordinal));
        var getLabelId = symbolIds.First(id => id.StartsWith("M:", StringComparison.Ordinal) && id.Contains("GetLabel"));

        var widgetDecl = store.GetSymbolSource(widgetClassId, snapshotId, ViewKind.Declaration);
        Assert.NotNull(widgetDecl);
        Assert.Contains("GetLabel", widgetDecl);

        var nameView = store.GetSymbolSource(widgetClassId, snapshotId, ViewKind.Name);
        Assert.NotNull(nameView);
        Assert.Equal("Widget", nameView);

        var sigView = store.GetSymbolSource(getLabelId, snapshotId, ViewKind.Signature);
        Assert.NotNull(sigView);

        var bodyView = store.GetSymbolSource(getLabelId, snapshotId, ViewKind.Body);
        Assert.NotNull(bodyView);
        Assert.Contains("Name", bodyView);

        var target = queries.Navigate("Library/Widget.cs", 4, snapshotId);
        Assert.NotNull(target);
        Assert.Equal("Library/Widget.cs", target.DocumentPath);

        var sourceResults = store.SearchSource("GetLabel", snapshotId);
        Assert.Contains(sourceResults, r => r.DocumentPath == "Library/Widget.cs");

        var lineStartsJson = QueryLineStarts(conn, snapshotId, "Library/Widget.cs");
        Assert.NotNull(lineStartsJson);
        Assert.Contains("0", lineStartsJson);
    }

    [SkippableFact]
    public async Task FullIndex_Utf16BeSource_NormalizesBytesAndPreservesAllViews()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system.");

        var bigEndian = Encoding.BigEndianUnicode;
        var preamble = bigEndian.GetPreamble();
        var contentBytes = bigEndian.GetBytes(WidgetContent);
        var encoded = new byte[preamble.Length + contentBytes.Length];
        Buffer.BlockCopy(preamble, 0, encoded, 0, preamble.Length);
        Buffer.BlockCopy(contentBytes, 0, encoded, preamble.Length, contentBytes.Length);

        var (dbPath, solutionPath, outputDir) = SetupEncodedFixture(encoded);
        var snapshotId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        var expectedBytes = Encoding.UTF8.GetBytes(WidgetContent);

        var storedBytes = QueryContentBytes(conn, snapshotId, "Library/Widget.cs");
        Assert.NotNull(storedBytes);
        Assert.Equal(expectedBytes, storedBytes);

        var encoding = QueryEncoding(conn, snapshotId, "Library/Widget.cs");
        Assert.Equal("utf-8", encoding);

        using var store = IntegrationHarness.OpenReadStore(dbPath);
        var queries = new FastTravelQueries(store);

        var source = queries.GetDocument("Library/Widget.cs", snapshotId);
        Assert.NotNull(source);
        Assert.Equal(WidgetContent, source);

        var symbolIds = QuerySymbolIds(conn, snapshotId, "Library/Widget.cs");
        Assert.NotEmpty(symbolIds);

        var widgetClassId = symbolIds.First(id => id.StartsWith("T:", StringComparison.Ordinal));
        var getLabelId = symbolIds.First(id => id.StartsWith("M:", StringComparison.Ordinal) && id.Contains("GetLabel"));

        var widgetDecl = store.GetSymbolSource(widgetClassId, snapshotId, ViewKind.Declaration);
        Assert.NotNull(widgetDecl);
        Assert.Contains("GetLabel", widgetDecl);

        var nameView = store.GetSymbolSource(widgetClassId, snapshotId, ViewKind.Name);
        Assert.NotNull(nameView);
        Assert.Equal("Widget", nameView);

        var sigView = store.GetSymbolSource(getLabelId, snapshotId, ViewKind.Signature);
        Assert.NotNull(sigView);

        var bodyView = store.GetSymbolSource(getLabelId, snapshotId, ViewKind.Body);
        Assert.NotNull(bodyView);
        Assert.Contains("Name", bodyView);

        var target = queries.Navigate("Library/Widget.cs", 4, snapshotId);
        Assert.NotNull(target);
        Assert.Equal("Library/Widget.cs", target.DocumentPath);

        var sourceResults = store.SearchSource("GetLabel", snapshotId);
        Assert.Contains(sourceResults, r => r.DocumentPath == "Library/Widget.cs");
    }

    private static byte[]? QueryContentBytes(SqliteConnection conn, string snapshotId, string relativePath)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT dv.content
            FROM snapshot_documents sd
            JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
            JOIN documents d ON d.document_id = dv.document_id
            WHERE sd.snapshot_id = @snapshotId AND d.relative_path = @relativePath;
        ";
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("@relativePath", relativePath);

        var result = cmd.ExecuteScalar();
        if (result == null || result == DBNull.Value)
            return null;
        return (byte[])result;
    }

    private static string? QueryEncoding(SqliteConnection conn, string snapshotId, string relativePath)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT dv.encoding
            FROM snapshot_documents sd
            JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
            JOIN documents d ON d.document_id = dv.document_id
            WHERE sd.snapshot_id = @snapshotId AND d.relative_path = @relativePath;
        ";
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("@relativePath", relativePath);

        var result = cmd.ExecuteScalar();
        return result as string;
    }

    private static string[] QuerySymbolIds(SqliteConnection conn, string snapshotId, string relativePath)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT d.symbol_id
            FROM declarations d
            JOIN document_versions dv ON dv.document_version_id = d.document_version_id
            JOIN documents doc ON doc.document_id = dv.document_id
            JOIN snapshot_documents sd ON sd.document_version_id = dv.document_version_id
            WHERE sd.snapshot_id = @snapshotId AND doc.relative_path = @relativePath;
        ";
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("@relativePath", relativePath);

        var ids = new System.Collections.Generic.List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids.ToArray();
    }

    private static string? QueryLineStarts(SqliteConnection conn, string snapshotId, string relativePath)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT dv.line_starts
            FROM snapshot_documents sd
            JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
            JOIN documents d ON d.document_id = dv.document_id
            WHERE sd.snapshot_id = @snapshotId AND d.relative_path = @relativePath;
        ";
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("@relativePath", relativePath);

        var result = cmd.ExecuteScalar();
        return result as string;
    }

    private static void RunGitCommand(string workingDir, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(30000);
    }

    private static void RunDotNetBuild(string solutionPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"build \"{solutionPath}\" --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(60000);
    }
}
