using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class IndexHandler
{
    public static async Task Run(string[] args, CancellationToken cancellationToken = default)
    {
        var solutionPathArg = HandlerBootstrap.GetArgValue(args, "--solution=")
                              ?? Environment.GetEnvironmentVariable("LURP_SOLUTION_PATH");
        if (string.IsNullOrEmpty(solutionPathArg) || !File.Exists(solutionPathArg)) HandlerBootstrap.Fail("ERROR: --solution=path or LURP_SOLUTION_PATH is required and must point to an existing .sln or .slnx file.");

        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);

        var outputDir = Path.GetFullPath(outputDirArg);
        Directory.CreateDirectory(outputDir);
        var dbPath = Path.Combine(outputDir, "index.db");
        var jsonExportPath = HandlerBootstrap.GetArgValue(args, "--output-json=");

        var skipAdapters = args.Where(a => a.StartsWith("--skip-adapter="))
            .Select(a => a.Split('=', 2)[1])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (skipAdapters.Count > 0)
        {
            var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ASP.NET Core", "Dependency Injection", "MediatR", "EF Core", "Serialization", "Test"
            };
            foreach (var name in skipAdapters.Where(name => !knownNames.Contains(name)))
                Console.WriteLine($"WARNING: Unknown adapter name '{name}'. Valid names: {string.Join(", ", knownNames)}");
            Console.WriteLine($"Skipping adapters: {string.Join(", ", skipAdapters)}");
        }

        var strategyArg = HandlerBootstrap.GetArgValue(args, "--strategy=");
        if (strategyArg != null)
        {
            var strategy = strategyArg.ToLowerInvariant();
            if (strategy is not ("incremental" or "full")) HandlerBootstrap.Fail("ERROR: --strategy must be 'incremental' or 'full'.");
        }

        var verbose = args.Contains("--verbose");
        var skipDiff = args.Contains("--skip-diff");
        var force = args.Contains("--force");

        Console.WriteLine($"Solution: {solutionPathArg}");
        Console.WriteLine($"Output DB: {dbPath}");
        if (jsonExportPath != null)
            Console.WriteLine($"JSON export: {jsonExportPath}");
        Console.WriteLine();

        var store = HandlerBootstrap.OpenStore(dbPath);
        store.RunMigrations();
        store.ValidateSchema(VersionConstants.DatabaseSchemaVersion);

        try
        {
            await IndexRunner.RunAsync(store, solutionPathArg, outputDir, skipAdapters, jsonExportPath, strategyArg, verbose, null, skipDiff, force, cancellationToken);
        }
        finally
        {
            store.Close();
        }
    }
}