using Lurp.Parity.Shared;
using Lurp.Storage;
using Microsoft.Data.Sqlite;

if (args.Length != 2)
    return DataError("Usage: r1-compare <incremental-db> <full-db>");

foreach (var path in args)
{
    if (!File.Exists(path))
        return DataError($"File not found: {path}");
}

try
{
    using var incremental = new SqliteIndexStore(args[0]);
    using var full = new SqliteIndexStore(args[1]);
    incremental.OpenReadOnly();
    full.OpenReadOnly();

    var incrementalId = incremental.LoadLatestSnapshot()?.SnapshotId;
    var fullId = full.LoadLatestSnapshot()?.SnapshotId;
    if (incrementalId is null)
        return DataError($"No snapshot in {args[0]}");
    if (fullId is null)
        return DataError($"No snapshot in {args[1]}");

    var differences = SnapshotComparison.Compare(args[0], incrementalId, args[1], fullId);
    if (differences.Count != 0)
    {
        foreach (var difference in differences)
            Console.Error.WriteLine(difference);
        return 1;
    }

    Console.WriteLine($"PASS: incremental ≡ full ({incremental.CountEdges(incrementalId)} edges, " +
                      $"{incremental.CountSymbolsInSnapshot(incrementalId)} symbols)");
    return 0;
}
catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
{
    return DataError(ex.Message);
}

static int DataError(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}
