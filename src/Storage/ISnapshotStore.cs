namespace Lurp.Storage
{
    public interface ISnapshotStore
    {
        void Open();
        void Close();
        bool IsOpen { get; }
        void RunMigrations();
        int GetCurrentSchemaVersion();
        void ValidateSchema(int expectedVersion);

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
        string? GetSource(string relativePath, string snapshotId);
        List<string> GetSnapshotIds(string workspaceId);

        void SaveSnapshotDocuments(string snapshotId, IEnumerable<(string DocumentId, string DocumentVersionId)> entries);
        Dictionary<string, string> GetDocumentVersionIdsByPath(string snapshotId);
        List<string> GetDocumentVersionIdsForDocuments(string snapshotId, IEnumerable<string> documentPaths);

        void SaveSnapshotSymbols(string snapshotId, IEnumerable<string> symbolIds);
        void CopySnapshotSymbols(string fromSnapshotId, string toSnapshotId);
        void DeleteSnapshotSymbolsBySymbolIds(string snapshotId, IEnumerable<string> symbolIds);
        List<string> GetSymbolIdsInSnapshot(string snapshotId);

        void DeleteIncompleteSnapshots();
        void PruneOldSnapshots(int keep = 3);
        void DeleteSnapshotData(string snapshotId);

        void SaveTimings(string snapshotId, IEnumerable<SnapshotTimingRow> timings);
        List<SnapshotTimingRow> GetTimings(string snapshotId);
    }
}
