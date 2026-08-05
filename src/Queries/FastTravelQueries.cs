using Lurp.Storage;

namespace Lurp.Queries;

public sealed class FastTravelQueries
{
    private readonly IDeclarationStore _declarations;
    private readonly ISnapshotDocumentStore _snapshots;

    public FastTravelQueries(IDeclarationStore declarations, ISnapshotDocumentStore snapshots)
    {
        _declarations = declarations ?? throw new ArgumentNullException(nameof(declarations));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
    }

    public string? GetDocument(string relativePath, string snapshotId) =>
        _snapshots.GetSource(relativePath, snapshotId);

    public IndexedSymbolInfo? GetSymbol(string symbolId, string snapshotId) =>
        _declarations.GetSymbolInfo(symbolId, snapshotId);

    public string? GetSymbolView(string symbolId, string snapshotId, ViewKind viewKind, bool includeGenerated = false) =>
        viewKind switch
        {
            ViewKind.ContainingType => _declarations.GetContainingTypeSource(symbolId, snapshotId),
            ViewKind.Surrounding => _declarations.GetSurroundingLines(symbolId, snapshotId, 3),
            _ => _declarations.GetSymbolSource(symbolId, snapshotId, viewKind, includeGenerated),
        };

    public NavigationTarget? Navigate(string relativePath, int line, string snapshotId, bool includeGenerated = false) =>
        _declarations.NavigateToLocation(relativePath, line, snapshotId, includeGenerated);
}
