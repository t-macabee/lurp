namespace Lurp.Tests;

internal static class TestRepoRoot
{
    internal static string Find()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "src", "Lurp.csproj")))
                return dir;
            var parent = Directory.GetParent(dir);
            if (parent == null)
                break;
            dir = parent.FullName;
        }

        var cur = Directory.GetCurrentDirectory();
        while (cur != null)
        {
            if (File.Exists(Path.Combine(cur, "src", "Lurp.csproj")))
                return cur;
            cur = Directory.GetParent(cur)?.FullName;
        }

        throw new InvalidOperationException("Could not locate repo root (src/Lurp.csproj).");
    }
}
