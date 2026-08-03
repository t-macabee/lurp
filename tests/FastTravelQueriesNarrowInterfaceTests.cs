using Lurp.Queries;
using Lurp.Storage;

namespace Lurp.Storage.Tests;

public sealed class FastTravelQueriesNarrowInterfaceTests
{
    [Fact]
    public void DocumentLookup_ForwardsRequestedSnapshotThroughSnapshotStore()
    {
        var snapshots = new RecordingSnapshotStore();
        var queries = new FastTravelQueries(new RecordingDeclarationStore(), snapshots);

        var document = queries.GetDocument("Library/Widget.cs", "snap-B");

        Assert.Equal("Library/Widget.cs@snap-B", document);
        Assert.Equal(["snap-B"], snapshots.GetSourceSnapshots);
    }

    [Fact]
    public void SymbolLookup_ForwardsRequestedSnapshotThroughDeclarationStore()
    {
        var declarations = new RecordingDeclarationStore();
        var queries = new FastTravelQueries(declarations, new RecordingSnapshotStore());

        var info = queries.GetSymbol("T:Widget|asm", "snap-B");

        Assert.NotNull(info);
        Assert.Equal(["snap-B"], declarations.GetSymbolInfoSnapshots);
        Assert.Equal("T:Widget|snap-B", info!.SymbolId.Value);
        Assert.Contains("snap-B", info.FullyQualifiedName);
    }

    [Fact]
    public void SourceViews_ForwardsRequestedSnapshotThroughDeclarationStore()
    {
        var declarations = new RecordingDeclarationStore();
        var queries = new FastTravelQueries(declarations, new RecordingSnapshotStore());

        var declaration = queries.GetSymbolView("T:Widget|asm", "snap-B", ViewKind.Declaration);
        var signature = queries.GetSymbolView("T:Widget|asm", "snap-B", ViewKind.Signature, includeGenerated: true);
        var containingType = queries.GetSymbolView("T:Widget|asm", "snap-B", ViewKind.ContainingType);
        var surrounding = queries.GetSymbolView("T:Widget|asm", "snap-B", ViewKind.Surrounding);

        Assert.Equal("Declaration@snap-B", declaration);
        Assert.Equal("Signature@snap-B", signature);
        Assert.Equal("containing@snap-B", containingType);
        Assert.Equal("surrounding-3@snap-B", surrounding);
        Assert.Equal(["snap-B", "snap-B"], declarations.GetSymbolSourceSnapshots);
        Assert.Equal(["snap-B"], declarations.GetContainingTypeSourceSnapshots);
        Assert.Equal(["snap-B"], declarations.GetSurroundingLinesSnapshots);
        Assert.Contains(("T:Widget|asm", "snap-B", ViewKind.Signature, true), declarations.GetSymbolSourceCalls);
    }

    [Fact]
    public void Navigate_ForwardsRequestedSnapshotAndLocationThroughDeclarationStore()
    {
        var declarations = new RecordingDeclarationStore();
        var queries = new FastTravelQueries(declarations, new RecordingSnapshotStore());

        var target = queries.Navigate("Library/Widget.cs", 6, "snap-B", includeGenerated: true);

        Assert.NotNull(target);
        Assert.Equal("Library/Widget.cs@snap-B", target!.DocumentPath);
        Assert.Equal([("Library/Widget.cs", 6, "snap-B", true)], declarations.NavigateCalls);
    }

    [Fact]
    public void Results_AreKeyedToRequestedSnapshot_NotHardcoded()
    {
        var queries = new FastTravelQueries(new RecordingDeclarationStore(), new RecordingSnapshotStore());

        var first = queries.GetDocument("Library/Widget.cs", "snap-A");
        var second = queries.GetDocument("Library/Widget.cs", "snap-B");

        Assert.Equal("Library/Widget.cs@snap-A", first);
        Assert.Equal("Library/Widget.cs@snap-B", second);
    }

    private sealed class RecordingDeclarationStore : IDeclarationStore
    {
        public List<string> GetSymbolInfoSnapshots { get; } = new();
        public List<string> GetSymbolSourceSnapshots { get; } = new();
        public List<string> GetContainingTypeSourceSnapshots { get; } = new();
        public List<string> GetSurroundingLinesSnapshots { get; } = new();
        public List<(string RelativePath, int Line, string SnapshotId, bool IncludeGenerated)> NavigateCalls { get; } = new();
        public List<(string SymbolId, string SnapshotId, ViewKind ViewKind, bool IncludeGenerated)> GetSymbolSourceCalls { get; } = new();

        public IndexedSymbolInfo? GetSymbolInfo(string symbolId, string snapshotId)
        {
            GetSymbolInfoSnapshots.Add(snapshotId);
            var pipeIndex = symbolId.IndexOf('|');
            var id = new SymbolId(symbolId[..pipeIndex], snapshotId);
            return new IndexedSymbolInfo(id, IndexedSymbolKind.Type, $"X@{snapshotId}", null, 1, false);
        }

        public string? GetSymbolSource(string symbolId, string snapshotId, ViewKind viewKind, bool includeGenerated = false)
        {
            GetSymbolSourceSnapshots.Add(snapshotId);
            GetSymbolSourceCalls.Add((symbolId, snapshotId, viewKind, includeGenerated));
            return $"{viewKind}@{snapshotId}";
        }

        public string? GetContainingTypeSource(string symbolId, string snapshotId)
        {
            GetContainingTypeSourceSnapshots.Add(snapshotId);
            return $"containing@{snapshotId}";
        }

        public string? GetSurroundingLines(string symbolId, string snapshotId, int contextLines)
        {
            GetSurroundingLinesSnapshots.Add(snapshotId);
            return $"surrounding-{contextLines}@{snapshotId}";
        }

        public NavigationTarget? NavigateToLocation(string relativePath, int line, string snapshotId, bool includeGenerated = false)
        {
            NavigateCalls.Add((relativePath, line, snapshotId, includeGenerated));
            return new NavigationTarget("T:Widget|asm", $"{relativePath}@{snapshotId}", "dv-1", 0, 10, 0, 5);
        }

        public void SaveDeclarations(string snapshotId, IEnumerable<SymbolDeclaration> declarations) => throw new NotSupportedException();
        public List<DeclarationLocation> GetDeclarationLocations(string symbolId, string snapshotId, bool includeGenerated = false) => throw new NotSupportedException();
        public void DeleteDeclarationsByDocumentVersionIds(IEnumerable<string> documentVersionIds) => throw new NotSupportedException();
        public List<string> GetSymbolIdsByDocumentVersionIds(string snapshotId, IEnumerable<string> documentVersionIds) => throw new NotSupportedException();
        public string? ResolveSymbolByLocation(string relativePath, int line, string snapshotId, bool includeGenerated = false) => throw new NotSupportedException();
    }

    private sealed class RecordingSnapshotStore : ISnapshotStore
    {
        public List<string> GetSourceSnapshots { get; } = new();

        public string? GetSource(string relativePath, string snapshotId)
        {
            GetSourceSnapshots.Add(snapshotId);
            return $"{relativePath}@{snapshotId}";
        }

        public void Open() => throw new NotSupportedException();
        public void Close() => throw new NotSupportedException();
        public bool IsOpen => throw new NotSupportedException();
        public void RunMigrations() => throw new NotSupportedException();
        public int GetCurrentSchemaVersion() => throw new NotSupportedException();
        public void ValidateSchema(int expectedVersion) => throw new NotSupportedException();
        public void SaveWorkspace(string id, string gitRoot, string solutionPath, DateTime createdAtUtc) => throw new NotSupportedException();
        public void SaveSnapshot(SnapshotRow manifest) => throw new NotSupportedException();
        public void MarkSnapshotInProgress(string snapshotId) => throw new NotSupportedException();
        public void MarkSnapshotComplete(string snapshotId) => throw new NotSupportedException();
        public void MarkSnapshotFailed(string snapshotId, string reasonCode, string? message) => throw new NotSupportedException();
        public SnapshotFailureRow? GetLatestSnapshotFailure(string? workspaceId = null) => throw new NotSupportedException();
        public SnapshotRow? LoadLatestSnapshot(string? workspaceId = null) => throw new NotSupportedException();
        public SnapshotRow? LoadSnapshotMetadata(string snapshotId) => throw new NotSupportedException();
        public string? GetLatestSnapshotId(string? workspaceId = null) => throw new NotSupportedException();
        public string? GetSnapshotGitRoot(string snapshotId) => throw new NotSupportedException();
        public string? GetSnapshotStatus(string snapshotId, string workspaceId) => throw new NotSupportedException();
        public List<string> GetSnapshotIds(string workspaceId) => throw new NotSupportedException();
        public void SaveSnapshotDocuments(string snapshotId, IEnumerable<(string DocumentId, string DocumentVersionId)> entries) => throw new NotSupportedException();
        public Dictionary<string, string> GetDocumentVersionIdsByPath(string snapshotId) => throw new NotSupportedException();
        public List<string> GetDocumentVersionIdsForDocuments(string snapshotId, IEnumerable<string> documentPaths) => throw new NotSupportedException();
        public void SaveSnapshotSymbols(string snapshotId, IEnumerable<string> symbolIds) => throw new NotSupportedException();
        public void CopySnapshotSymbols(string fromSnapshotId, string toSnapshotId) => throw new NotSupportedException();
        public void DeleteSnapshotSymbolsBySymbolIds(string snapshotId, IEnumerable<string> symbolIds) => throw new NotSupportedException();
        public List<string> GetSymbolIdsInSnapshot(string snapshotId) => throw new NotSupportedException();
        public int CountSymbolsInSnapshot(string snapshotId) => throw new NotSupportedException();
        public void DeleteIncompleteSnapshots() => throw new NotSupportedException();
        public void PruneOldSnapshots(int keep = 3) => throw new NotSupportedException();
        public void DeleteSnapshotData(string snapshotId) => throw new NotSupportedException();
        public void SaveTimings(string snapshotId, IEnumerable<SnapshotTimingRow> timings) => throw new NotSupportedException();
        public List<SnapshotTimingRow> GetTimings(string snapshotId) => throw new NotSupportedException();
    }
}
