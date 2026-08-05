namespace Lurp.Storage
{
    /// <summary>Connection and schema lifecycle for the backing database.</summary>
    public interface IStoreConnection
    {
        void Open();
        void Close();
        bool IsOpen { get; }
        void RunMigrations();
        int GetCurrentSchemaVersion();
        void ValidateSchema(int expectedVersion);
    }

    /// <summary>Workspace/snapshot manifest rows and their lifecycle status.</summary>
    public interface ISnapshotManifestStore
    {
        void SaveWorkspace(string id, string gitRoot, string solutionPath, DateTime createdAtUtc);
        void SaveSnapshot(SnapshotRow manifest);
        void MarkSnapshotInProgress(string snapshotId);
        void MarkSnapshotComplete(string snapshotId);
        void MarkSnapshotFailed(string snapshotId, string reasonCode, string? message);
        SnapshotFailureRow? GetLatestSnapshotFailure(string? workspaceId = null);
        SnapshotRow? LoadLatestSnapshot(string? workspaceId = null);
        SnapshotRow? LoadSnapshotMetadata(string snapshotId);
        string? GetLatestSnapshotId(string? workspaceId = null);
        string? GetSnapshotGitRoot(string snapshotId);
        string? GetSnapshotStatus(string snapshotId, string workspaceId);
        List<string> GetSnapshotIds(string workspaceId);
    }

    /// <summary>Document membership and source text for a snapshot.</summary>
    public interface ISnapshotDocumentStore
    {
        string? GetSource(string relativePath, string snapshotId);
        void SaveSnapshotDocuments(string snapshotId, IEnumerable<(string DocumentId, string DocumentVersionId)> entries);
        Dictionary<string, string> GetDocumentVersionIdsByPath(string snapshotId);
        List<string> GetDocumentVersionIdsForDocuments(string snapshotId, IEnumerable<string> documentPaths);
    }

    /// <summary>Symbol membership of a snapshot.</summary>
    public interface ISnapshotSymbolStore
    {
        void SaveSnapshotSymbols(string snapshotId, IEnumerable<string> symbolIds);
        void CopySnapshotSymbols(string fromSnapshotId, string toSnapshotId);
        void DeleteSnapshotSymbolsBySymbolIds(string snapshotId, IEnumerable<string> symbolIds);
        List<string> GetSymbolIdsInSnapshot(string snapshotId);
        int CountSymbolsInSnapshot(string snapshotId);
    }

    /// <summary>Retention and cleanup of snapshot data.</summary>
    public interface ISnapshotPruner
    {
        void DeleteIncompleteSnapshots();
        void PruneOldSnapshots(int keep = 3);
        void DeleteSnapshotData(string snapshotId);
    }

    /// <summary>Per-snapshot indexing phase timings.</summary>
    public interface ISnapshotTimingStore
    {
        void SaveTimings(string snapshotId, IEnumerable<SnapshotTimingRow> timings);
        List<SnapshotTimingRow> GetTimings(string snapshotId);
    }

    /// <summary>
    /// Composite of the snapshot-side seams. Kept as the single type implemented by
    /// <c>SqliteIndexStore</c> and inherited by <see cref="IIndexStore"/>; consumers
    /// should depend on the narrowest sub-interface they actually use.
    /// </summary>
    public interface ISnapshotStore
        : IStoreConnection,
          ISnapshotManifestStore,
          ISnapshotDocumentStore,
          ISnapshotSymbolStore,
          ISnapshotPruner,
          ISnapshotTimingStore
    {
    }
}
