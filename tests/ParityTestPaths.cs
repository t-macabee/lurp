namespace Lurp.Tests;

internal static class ParityTestPaths
{
    internal static string BuiltTool(string projectPath, string name)
    {
        var framework = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = framework.Parent!.Name;
        return Path.Combine(TestRepoRoot.Find(), projectPath, "bin", configuration, framework.Name, name + ".dll");
    }
}
