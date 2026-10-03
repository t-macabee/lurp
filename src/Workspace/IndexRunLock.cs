using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Lurp.Workspace;

/// <summary>
/// Provides file-based locking for Lurp index runs, preventing concurrent indexing of the same
/// solution or database across both CLI and MCP entry points. Locks are held in the user's temp
/// folder with Windows case-folding for key normalization.
/// </summary>
internal static class IndexRunLock
{
    private const string LockDirName = "lurp-locks";

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint FILE_SHARE_DELETE = 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint VOLUME_NAME_DOS = 0x0;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle hFile, [Out] char[] lpszFilePath,
        uint cchFilePath, uint dwFlags);

    /// <summary>
    /// Normalize a path key for locking: resolve links, junctions, and substituted
    /// drives to their final on-disk path, then apply Windows case-folding.
    /// Two spellings of one path must hash to one lock file, or two runs can
    /// write the same database concurrently.
    /// </summary>
    public static string NormalizeKey(string fullPath)
    {
        var canonical = Canonicalize(fullPath);
        return OperatingSystem.IsWindows() ? canonical.ToLowerInvariant() : canonical;
    }

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

    /// <summary>
    /// Resolves the deepest existing ancestor directory through the OS
    /// (<c>GetFinalPathNameByHandle</c>) and re-appends the not-yet-existing tail.
    /// This folds <c>subst</c> drives, junctions, symlinks, 8.3 names, and case
    /// variants onto one key. Falls back to the full path when resolution fails.
    /// </summary>
    private static string Canonicalize(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { return path; }

        if (!OperatingSystem.IsWindows())
            return full;

        var tail = new List<string>();
        var dir = Path.GetDirectoryName(full);
        var name = Path.GetFileName(full);

        while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            if (!string.IsNullOrEmpty(name))
                tail.Add(name);
            name = Path.GetFileName(dir);
            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
            {
                dir = null;
                break;
            }

            dir = parent;
        }

        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return full;

        var resolved = TryGetFinalPath(dir);
        if (string.IsNullOrEmpty(resolved))
            return full;

        var result = resolved!;
        if (!string.IsNullOrEmpty(name))
            result = Path.Combine(result, name);
        for (var i = tail.Count - 1; i >= 0; i--)
            result = Path.Combine(result, tail[i]);

        return result;
    }

    private static string? TryGetFinalPath(string directory)
    {
        using var handle = CreateFile(directory, GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;

        var buffer = new char[1024];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, VOLUME_NAME_DOS);
        if (length == 0 || length >= (uint)buffer.Length)
            return null;

        var result = new string(buffer, 0, (int)length);
        // Strip the extended-length prefix: \\?\C:\... or \\?\UNC\server\share\...
        if (result.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return @"\\" + result[8..];
        if (result.StartsWith(@"\\?\", StringComparison.Ordinal))
            return result[4..];
        return result;
    }
}
