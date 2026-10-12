using Lurp.Tests.Docs;

namespace Lurp.Tests;

public sealed class DocsInSyncTests
{
    [Fact]
    public void GeneratedSections_MatchTheCode()
    {
        var root = DocCatalog.RepoRoot();
        var failures = new List<string>();

        foreach (var section in DocCatalog.Sections)
        {
            var path = Path.Combine(root, section.RelativePath);
            var original = File.ReadAllText(path);
            var hadCrlf = original.Contains("\r\n");
            var text = original.Replace("\r\n", "\n");

            var current = DocSections.Extract(text, section.Key);
            if (current is null)
            {
                failures.Add($"{section.RelativePath} :: {section.Key} :: marker missing");
                continue;
            }

            var expected = section.Render();
            if (current == expected)
                continue;

            if (!DocCatalog.UpdateMode)
            {
                failures.Add($"{section.RelativePath} :: {section.Key} :: differs");
                continue;
            }

            var updated = DocSections.Replace(text, section.Key, expected);
            if (hadCrlf)
                updated = updated.Replace("\n", "\r\n");
            File.WriteAllText(path, updated);
        }

        Assert.True(failures.Count == 0,
            string.Join(Environment.NewLine, failures) + Environment.NewLine +
            "Regenerate: $env:LURP_UPDATE_DOCS = '1'; dotnet test tests/Lurp.Tests.csproj --filter \"FullyQualifiedName~DocsInSyncTests\"; then review the diff of the docs.");
    }

    [Fact]
    public void EveryMarkerInTheDocs_HasARenderer()
    {
        var root = DocCatalog.RepoRoot();
        var files = new List<string>
        {
            Path.Combine(root, "README.md"),
            Path.Combine(root, "VERSIONING.md"),
            Path.Combine(root, "CONTRIBUTING.md"),
        };
        files.AddRange(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md"));
        files.AddRange(Directory.EnumerateFiles(Path.Combine(root, "notes"), "*.md"));

        var known = DocCatalog.Sections
            .Select(section => (File: Normalize(section.RelativePath), section.Key))
            .ToHashSet();

        var failures = new List<string>();
        var markerCount = 0;
        foreach (var file in files)
        {
            var relative = Normalize(Path.GetRelativePath(root, file));
            foreach (var key in DocSections.FindKeys(File.ReadAllText(file)))
            {
                markerCount++;
                if (!known.Contains((relative, key)))
                    failures.Add($"{relative} :: {key} :: no renderer");
            }
        }

        Assert.True(markerCount >= 3, $"Expected at least 3 generated markers, found {markerCount}.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
