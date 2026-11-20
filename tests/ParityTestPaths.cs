namespace Lurp.Tests;

internal static class ParityTestPaths
{
    internal static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Lurp.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Could not locate Lurp.slnx.");
    }

    internal static string BuiltTool(string projectPath, string name)
    {
        var framework = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = framework.Parent!.Name;
        return Path.Combine(RepositoryRoot(), projectPath, "bin", configuration, framework.Name, name + ".dll");
    }
}
