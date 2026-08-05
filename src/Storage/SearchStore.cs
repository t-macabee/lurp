// Purpose: facade composing the three halves of the search store behind ISearchStore.
// Owns: the ISearchStore contract and delegation to source/symbol/index-maintenance units.
// Must not contain: SQL, result shaping, or any Roslyn dependency.

using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

public sealed class SearchStore : ISearchStore
{
    private readonly SearchSourceStore _source;
    private readonly SearchSymbolStore _symbols;
    private readonly SearchIndexMaintenance _maintenance;

    public SearchStore(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _source = new SearchSourceStore(connection);
        _symbols = new SearchSymbolStore(connection);
        _maintenance = new SearchIndexMaintenance(connection);
    }

    /// <inheritdoc/>
    public void BuildSearchIndex(string snapshotId)
        => _maintenance.BuildSearchIndex(snapshotId);

    /// <inheritdoc/>
    public void BuildSearchIndex(string snapshotId, HashSet<string> changedDocumentPaths, HashSet<string> changedSymbolIds)
        => _maintenance.BuildSearchIndex(snapshotId, changedDocumentPaths, changedSymbolIds);

    /// <inheritdoc/>
    public void CopySearchIndexToSnapshot(string fromSnapshotId, string toSnapshotId)
        => _maintenance.CopySearchIndexToSnapshot(fromSnapshotId, toSnapshotId);

    /// <inheritdoc/>
    public List<SourceSearchResult> SearchSource(string query, string snapshotId, int limit = 20, bool includeGenerated = false, int snippetTokens = 64)
        => _source.SearchSource(query, snapshotId, limit, includeGenerated, snippetTokens);

    /// <inheritdoc/>
    public List<SymbolSearchResult> SearchSymbols(string query, string snapshotId, int limit = 20, bool includeGenerated = false, string? kind = null)
        => _symbols.SearchSymbols(query, snapshotId, limit, includeGenerated, kind);

    /// <inheritdoc/>
    public SymbolSearchPage SearchSymbolsPage(string query, string snapshotId, int limit, bool includeGenerated, string? kind, SearchCursor? cursor)
        => _symbols.SearchSymbolsPage(query, snapshotId, limit, includeGenerated, kind, cursor);

    /// <inheritdoc/>
    public IndexedSymbolInfo? ResolveSymbolByFqn(string fqn, string snapshotId, bool includeGenerated = false)
        => _symbols.ResolveSymbolByFqn(fqn, snapshotId, includeGenerated);
}
