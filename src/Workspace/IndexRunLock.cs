using System.Security.Cryptography;
using System.Text;

namespace Lurp.Workspace;

/// <summary>
/// Provides file-based locking for Lurp index runs, preventing concurrent indexing of the same
/// solution or database across both CLI and MCP entry points. Locks are held in the user's temp
/// folder, keyed by the canonical path identity computed by <see cref="PathIdentity"/>.
/// </summary>
internal static class IndexRunLock
{
    private const string LockDirName = "lurp-locks";

    /// <summary>
    /// Normalize a path key for locking: resolve links, junctions, and substituted
    /// drives to their final on-disk path, then apply Windows case-folding.
    /// Two spellings of one path must hash to one lock file, or two runs can
    /// write the same database concurrently.
    /// </summary>
    public static string NormalizeKey(string fullPath) => PathIdentity.CanonicalKey(fullPath);

    /// <summary>
    /// Try to acquire a lock for the given key. Returns a FileStream if successful,
    /// or null if another process holds the lock. The returned stream should be disposed
    /// to release the lock (FileOptions.DeleteOnClose ensures cleanup).
    /// </summary>
    public static FileStream? TryAcquire(string key)
    {
        var lockPath = GetLockFilePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Compute the lock file path for a given key (for testing).
    /// </summary>
    public static string GetLockFilePath(string key)
    {
        var lockDir = Path.Combine(Path.GetTempPath(), LockDirName);
        var lockName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(lockDir, lockName);
    }
}
