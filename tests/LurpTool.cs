namespace Lurp.Tests;

internal static class LurpTool
{
    internal static (string FileName, string? DllPath) ResolveCommand()
    {
        var packed = Environment.GetEnvironmentVariable("LURP_TOOL_PATH");
        if (!string.IsNullOrEmpty(packed))
            return (Path.GetFullPath(packed), null);

        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "Lurp.dll");
            if (File.Exists(candidate))
                return ("dotnet", candidate);
            dir = Directory.GetParent(dir)?.FullName;
        }

        var current = Directory.GetCurrentDirectory();
        while (current != null)
        {
            var bin = Path.Combine(current, "src", "bin");
            if (Directory.Exists(bin))
            {
                var matches = Directory.GetFiles(bin, "Lurp.dll", SearchOption.AllDirectories);
                if (matches.Length > 0)
                {
                    var release = matches.FirstOrDefault(m => m.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}"));
                    return ("dotnet", release ?? matches.OrderByDescending(File.GetLastWriteTimeUtc).First());
                }
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new InvalidOperationException("Could not locate Lurp.dll (or set LURP_TOOL_PATH to a packed lurp executable).");
    }

    internal static string[] Argv(params string[] args)
    {
        var (fileName, dllPath) = ResolveCommand();
        return dllPath == null ? [fileName, .. args] : [fileName, dllPath, .. args];
    }
}
