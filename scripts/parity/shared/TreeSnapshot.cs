using System.Security.Cryptography;

namespace Lurp.Parity.Shared;

/// <summary>
///     Records every file (path, size, SHA-256) plus the directory list, then diffs
///     two recordings. Shared between the integrity tests and the parity gate so
///     the two never drift apart.
/// </summary>
public static class TreeSnapshot
{
    /// <summary>Path plus size plus SHA-256 for every file, and every directory path.</summary>
    public static SortedDictionary<string, string> Record(string root)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            map["dir: " + Path.GetRelativePath(root, dir).Replace('\\', '/')] = "";

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            using var stream = info.OpenRead();
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            map[Path.GetRelativePath(root, file).Replace('\\', '/')] = $"{info.Length}|{hash}";
        }

        return map;
    }

    /// <summary>
    ///     Compares two recordings. Changed entries carry the old and new
    ///     "size|hash" values.
    /// </summary>
    public static TreeDiff Diff(SortedDictionary<string, string> before, SortedDictionary<string, string> after)
    {
        var added = after.Keys.Except(before.Keys, StringComparer.Ordinal).ToList();
        var removed = before.Keys.Except(after.Keys, StringComparer.Ordinal).ToList();
        var changed = before.Keys
            .Where(k => after.TryGetValue(k, out var v) && v != before[k])
            .Select(k => (k, before[k], after[k]))
            .ToList();
        return new TreeDiff(added, removed, changed);
    }
}

/// <summary>
///     The result of diffing two tree snapshots: added paths, removed paths, and
///     changed paths with their old and new values.
/// </summary>
public sealed record TreeDiff(
    List<string> Added,
    List<string> Removed,
    List<(string Path, string OldValue, string NewValue)> Changed);
