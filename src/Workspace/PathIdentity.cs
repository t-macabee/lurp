using Lurp.Shared;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Lurp.Workspace;

/// <summary>
/// Canonical identity of a filesystem path: the final on-disk path with Windows
/// case-folding applied. This is the single source of the key the run locks and
/// the default cache directory hash, so a change here moves both.
/// </summary>
internal static class PathIdentity
{
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
    /// Canonical key for a filesystem path. Resolve links, junctions, and
    /// substituted drives to their final on-disk path, then apply Windows
    /// case-folding, so two spellings of one path give one key.
    ///
    /// The key names the run-lock file: a second spelling would let two runs
    /// write one database. It also hashes the default cache directory, so a
    /// change here moves every user's index.
    /// </summary>
    public static string CanonicalKey(string path)
    {
        var canonical = Canonicalize(path);
        return OperatingSystem.IsWindows() ? canonical.ToLowerInvariant() : canonical;
    }

    /// <summary>
    /// Resolves the deepest existing ancestor directory through the OS
    /// (<c>GetFinalPathNameByHandle</c> on Windows, <c>realpath</c> on other
    /// OSes) and re-appends the not-yet-existing tail. On Windows this folds
    /// <c>subst</c> drives, junctions, symlinks, 8.3 names, and case variants
    /// onto one key; on other OSes it resolves symlinks. Falls back to the
    /// full path when resolution fails.
    /// </summary>
    private static string Canonicalize(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { return path; }

        var tail = new List<string>();
        string? dir;
        string name;

        if (Directory.Exists(full))
        {
            dir = full;
            name = string.Empty;
        }
        else
        {
            dir = Path.GetDirectoryName(full);
            name = Path.GetFileName(full);
        }

        while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            if (!string.IsNullOrEmpty(name))
                tail.Add(name);
            name = Path.GetFileName(dir);
            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, dir, PathNormalizer.PathComparison))
            {
                dir = null;
                break;
            }

            dir = parent;
        }

        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return full;

        string? resolved;
        if (OperatingSystem.IsWindows())
            resolved = TryGetFinalPath(dir);
        else
            resolved = TryRealpath(dir);

        if (string.IsNullOrEmpty(resolved))
            return full;

        var result = resolved!;
        if (!string.IsNullOrEmpty(name))
            result = Path.Combine(result, name);
        for (var i = tail.Count - 1; i >= 0; i--)
            result = Path.Combine(result, tail[i]);

        return result;
    }

    private const int PATH_MAX = 4096;

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr realpath(byte[] path, byte[] resolved_path);

    private static string? TryRealpath(string directory)
    {
        var pathBytes = new byte[Encoding.UTF8.GetByteCount(directory) + 1];
        Encoding.UTF8.GetBytes(directory, 0, directory.Length, pathBytes, 0);
        // null terminator already zero

        var buffer = new byte[PATH_MAX];
        var result = realpath(pathBytes, buffer);
        if (result == IntPtr.Zero)
            return null;

        var length = 0;
        while (length < buffer.Length && buffer[length] != 0)
            length++;

        if (length == 0)
            return null;

        return Encoding.UTF8.GetString(buffer, 0, length);
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
