using Lurp.Parity.Shared;

namespace Lurp.Tests;

internal static class SnapshotAssertions
{
    public static void CompareSnapshotsAreEquivalent(
        string dbPathB, string snapshotB, string dbPathC, string snapshotC)
    {
        var differences = SnapshotComparison.Compare(dbPathB, snapshotB, dbPathC, snapshotC);
        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences));
    }

    internal static List<SnapshotComparison.DocumentSnapshot> ReadDocuments(string dbPath, string snapshotId) =>
        SnapshotComparison.ReadDocuments(dbPath, snapshotId);

    internal static List<SnapshotComparison.SymbolSnapshot> ReadSymbols(string dbPath, string snapshotId) =>
        SnapshotComparison.ReadSymbols(dbPath, snapshotId);

    internal static List<SnapshotComparison.DeclarationSnapshot> ReadDeclarations(string dbPath, string snapshotId) =>
        SnapshotComparison.ReadDeclarations(dbPath, snapshotId);

    internal static List<SnapshotComparison.EdgeSiteSnapshot> ReadEdgeSites(string dbPath, string snapshotId) =>
        SnapshotComparison.ReadEdgeSites(dbPath, snapshotId);

    internal static List<string> ReadSnapshotGraphNodes(string dbPath, string snapshotId) =>
        SnapshotComparison.ReadSnapshotGraphNodes(dbPath, snapshotId);

    internal static List<SnapshotComparison.ProjectSnapshot> ReadProjects(string dbPath, string snapshotId) =>
        SnapshotComparison.ReadProjects(dbPath, snapshotId);

    internal static List<SnapshotComparison.ProjectReferenceSnapshot> ReadProjectReferences(string dbPath, string snapshotId) =>
        SnapshotComparison.ReadProjectReferences(dbPath, snapshotId);

    internal static List<string> ReadTargetFrameworksForSymbol(string dbPath, string snapshotId, string docCommentId) =>
        SnapshotComparison.ReadTargetFrameworksForSymbol(dbPath, snapshotId, docCommentId);
}
