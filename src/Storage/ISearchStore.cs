namespace Lurp.Storage
{
    public interface ISearchStore
    {
        /// <summary>
        /// Full FTS rebuild. Runs after all documents, symbol membership, semantic diff,
        /// and orphan cleanup are persisted for the snapshot, and must finish before
        /// <see cref="ISnapshotStore.MarkSnapshotComplete"/>.
        /// </summary>
        void BuildSearchIndex(string snapshotId);

        /// <summary>
        /// Incremental FTS refresh. Runs after <see cref="CopySearchIndexToSnapshot"/>
        /// seeded the new snapshot and after the final changed-document-path and
        /// changed-symbol-ID sets (including removals) are determined, and must finish
        /// before completion. Both sets may include items that need their FTS rows
        /// deleted and re-inserted.
        /// </summary>
        void BuildSearchIndex(string snapshotId, HashSet<string> changedDocumentPaths, HashSet<string> changedSymbolIds);

        /// <summary>
        /// Mandatory incremental seed. Copies every FTS row from the previous snapshot
        /// into the new snapshot so the subsequent refresh only needs to delete and
        /// re-insert the changed subset. Must be called before the incremental
        /// <see cref="BuildSearchIndex(string, HashSet{string}, HashSet{string})"/>
        /// overload for the same target snapshot.
        /// </summary>
        void CopySearchIndexToSnapshot(string fromSnapshotId, string toSnapshotId);

        List<SourceSearchResult> SearchSource(string query, string snapshotId, int limit = 20, bool includeGenerated = false, int snippetTokens = 64);
        List<SymbolSearchResult> SearchSymbols(string query, string snapshotId, int limit = 20, bool includeGenerated = false, string? kind = null);

        /// <summary>
        /// Keyset-paginated symbol search. Fetches one extra row beyond <paramref name="limit"/>
        /// to determine whether a next page exists, then returns an opaque cursor for it rather
        /// than an offset : offsets shift under duplicate-free re-ordering, cursors do not.
        /// </summary>
        SymbolSearchPage SearchSymbolsPage(string query, string snapshotId, int limit, bool includeGenerated, string? kind, SearchCursor? cursor);

        IndexedSymbolInfo? ResolveSymbolByFqn(string fqn, string snapshotId, bool includeGenerated = false);
    }
}
