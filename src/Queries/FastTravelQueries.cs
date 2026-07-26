using Lurp.Storage;

namespace Lurp.Queries;

public sealed class FastTravelQueries
{
    private readonly SqliteIndexStore _store;

    public FastTravelQueries(SqliteIndexStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string? GetDocument(string relativePath, string snapshotId) =>
        _store.GetSource(relativePath, snapshotId);

    public IndexedSymbolInfo? GetSymbol(string symbolId, string snapshotId) =>
        _store.GetSymbolInfo(symbolId, snapshotId);

    public string? GetSymbolView(string symbolId, string snapshotId, ViewKind viewKind, bool includeGenerated = false) =>
        viewKind switch
        {
            ViewKind.ContainingType => _store.GetContainingTypeSource(symbolId, snapshotId),
            ViewKind.Surrounding => _store.GetSurroundingLines(symbolId, snapshotId, 3),
            _ => _store.GetSymbolSource(symbolId, snapshotId, viewKind, includeGenerated),
        };

    public NavigationTarget? Navigate(string relativePath, int line, string snapshotId, bool includeGenerated = false) =>
        _store.NavigateToLocation(relativePath, line, snapshotId, includeGenerated);
}
