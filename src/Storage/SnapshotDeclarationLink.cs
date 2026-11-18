namespace Lurp.Storage;

/// <summary>
///     The snapshot-declaration link join (D44, D154). Every declaration reader uses this
///     helper to restrict <c>declarations</c> rows to those linked to the target snapshot.
/// </summary>
internal static class SnapshotDeclarationLink
{
    /// <summary>
    ///     Returns a <c>JOIN snapshot_declarations ...</c> clause linking
    ///     <paramref name="declarationAlias" /> to <paramref name="snapshotExpression" />.
    /// </summary>
    internal static string Join(
        string declarationAlias,
        string linkAlias = "sdecl",
        string snapshotExpression = "@snapshotId") =>
        $"JOIN snapshot_declarations {linkAlias} ON {linkAlias}.declaration_id = {declarationAlias}.declaration_id AND {linkAlias}.snapshot_id = {snapshotExpression}";
}
