namespace Lurp.Queries;

public sealed class FastTravelQueries
{
    private readonly IDeclarationStore _declarations;

    public FastTravelQueries(IDeclarationStore declarations)
    {
        _declarations = declarations ?? throw new ArgumentNullException(nameof(declarations));
    }

    public NavigationTarget? Navigate(string relativePath, int line, string snapshotId, bool includeGenerated = false)
    {
        return _declarations.NavigateToLocation(relativePath, line, snapshotId, includeGenerated);
    }
}