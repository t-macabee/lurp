using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Tests;

/// <summary>
///     D103: one version string means one extraction rule. The gate extracts three committed
///     corpora in memory (CallShapes, CrossProject, ExtractorGate), hashes the output of each
///     registered extractor, and compares (name, version, rows, hash) with
///     tests/Snapshots/extractor-versions.txt. The file is a change detector, not an oracle:
///     the review of its diff is the gate. It is also the one literal of every extractor and
///     adapter version string and of the aggregate ExtractorVersion (S00-F3, S10-F6).
/// </summary>
[Trait("Category", "Slow")]
[Collection("GroundTruth")]
public sealed class ExtractorVersionGateTests(
    GroundTruthFixture callShapes,
    CrossProjectGroundTruthFixture crossProject,
    ExtractorGateFixture gate)
{
    internal const string AggregateName = "ExtractorVersion";

    [Fact]
    public void EveryExtractorVersion_MatchesItsRecordedOutput()
    {
        var results = new GroundTruthFixtureBase[] { callShapes, crossProject, gate }
            .SelectMany(fixture => fixture.Results)
            .ToList();
        var problems = new List<string>();
        var actual = ComputeRows(results, problems);

        var snapshotDir = Path.Combine(TestRepoRoot.Find(), "tests", "Snapshots");
        var gatePath = Path.Combine(snapshotDir, "extractor-versions.txt");
        var actualPath = Path.Combine(snapshotDir, "extractor-versions.actual.txt");
        File.WriteAllText(actualPath, Format(actual));

        var expected = File.Exists(gatePath)
            ? Parse(File.ReadAllLines(gatePath))
            : new Dictionary<string, GateRow>(StringComparer.Ordinal);
        if (!File.Exists(gatePath))
            problems.Add($"{gatePath} does not exist.");

        foreach (var row in actual)
        {
            if (row.Rows == 0)
                problems.Add($"{row.Name} {row.Version}: the corpora give no row, so the gate cannot see a change. Add a shape for it to tests/Fixtures/GroundTruth/ExtractorGate.");

            if (!expected.TryGetValue(row.Name, out var old))
            {
                problems.Add($"{row.Name} {row.Version}: no row in the gate file. Copy the row from the .actual file.");
                continue;
            }

            var sameVersion = old.Version == row.Version;
            var sameOutput = old.Hash == row.Hash;
            if (sameVersion && !sameOutput)
                problems.Add($"{row.Name}: the output changed ({old.Rows} -> {row.Rows} rows) and the version {row.Version} did not. " +
                             "Bump the version. When this version is not released yet, or when only a corpus, the .NET SDK or a package changed, " +
                             "copy the row from the .actual file instead. Give the reason in the commit message.");
            else if (!sameVersion && sameOutput)
                problems.Add($"{row.Name}: the version moved {old.Version} -> {row.Version} and the output did not change. Revert the bump.");
            else if (!sameVersion)
                problems.Add($"{row.Name}: the version moved {old.Version} -> {row.Version} with new output. Copy the row from the .actual file.");
        }

        foreach (var name in expected.Keys.Except(actual.Select(row => row.Name), StringComparer.Ordinal))
            problems.Add($"{name}: the gate file has a row for an extractor that is not registered. Remove the row.");

        Assert.True(problems.Count == 0,
            $"Extractor version gate (D103, VERSIONING.md) failed:\n  {string.Join("\n  ", problems)}\nActual rows: {actualPath}");
    }

    internal static List<GateRow> ComputeRows(IReadOnlyList<CompilationFactExtractor.ExtractionResult> results, List<string> problems)
    {
        var registry = ExtractorRegistry.All;
        foreach (var duplicate in registry.GroupBy(e => e.Version, StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add($"version '{duplicate.Key}' is registered for {string.Join(", ", duplicate.Select(e => e.Name))}; one label must name one extractor.");

        var edges = results.SelectMany(r => r.Edges).ToList();
        var known = registry.Select(e => e.Version).ToHashSet(StringComparer.Ordinal);
        foreach (var label in edges.Select(e => e.ExtractorVersion).Where(v => !known.Contains(v)).Distinct().OrderBy(v => v, StringComparer.Ordinal))
            problems.Add($"edges carry the label '{label}', which no registered extractor has.");

        var rows = new List<GateRow>();
        var all = new List<string>();
        all.AddRange(edges.Select(e => "E\u001f" + e.ExtractorVersion + "\u001f" + EdgeRow(e)));
        all.AddRange(results.SelectMany(r => r.Declarations).Select(d => "D\u001f" + DeclarationRow(d)));
        all.AddRange(results.SelectMany(r => r.BindingIncompleteness).Select(b => "B\u001f" + Join(b.ProjectName, b.DocumentPath, b.Reason, b.Count, b.ExtractorVersion)));
        all.AddRange(results.SelectMany(r => r.Annotations).Select(a => "A\u001f" + Join(a.SymbolId, a.Kind, a.Value, a.DocumentPath)));
        rows.Add(MakeRow(AggregateName, VersionConstants.ExtractorVersion, all));

        foreach (var (name, version, _) in registry.OrderBy(e => e.Name, StringComparer.Ordinal))
            rows.Add(MakeRow(name, version, edges.Where(e => e.ExtractorVersion == version).Select(EdgeRow)));

        return rows;
    }

    private static string EdgeRow(EdgeRecord e) =>
        Join(e.SourceSymbolId, e.TargetSymbolId, e.Kind, e.Provenance, e.SourceDocumentPath,
            e.SourceStartLine, e.SourceStartColumn, e.SourceEndLine, e.SourceEndColumn, e.IsCrossGenerated,
            e.TypeArgumentsJson, e.ReceiverTypeConstraintsJson, e.SourceNodeKind, e.TargetNodeKind);

    private static string DeclarationRow(SymbolDeclaration d) =>
        Join(d.SymbolId.Value, d.Kind, d.DocumentVersionId, d.FullSpan, d.SignatureSpan, d.BodySpan, d.NameSpan,
            d.IsPartial, d.MetadataJson, d.IsGenerated, d.GeneratorIdentity, d.TargetFramework);

    private static string Join(params object?[] values) =>
        string.Join("\u001f", values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "␀"));

    private static GateRow MakeRow(string name, string version, IEnumerable<string> rows)
    {
        var sorted = rows.OrderBy(r => r, StringComparer.Ordinal).ToList();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", sorted))));
        return new GateRow(name, version, sorted.Count, hash);
    }

    private static string Format(IEnumerable<GateRow> rows) =>
        "# Extractor version gate (D103). Columns: name, version, rows, SHA-256 of the sorted rows.\n" +
        "# Written by ExtractorVersionGateTests. Review the diff; see VERSIONING.md.\n" +
        string.Concat(rows.Select(r => $"{r.Name}\t{r.Version}\t{r.Rows}\t{r.Hash}\n"));

    private static Dictionary<string, GateRow> Parse(IEnumerable<string> lines) =>
        lines.Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t'))
            .Select(p => new GateRow(p[0], p[1], int.Parse(p[2], CultureInfo.InvariantCulture), p[3]))
            .ToDictionary(r => r.Name, StringComparer.Ordinal);

    internal sealed record GateRow(string Name, string Version, int Rows, string Hash);
}
