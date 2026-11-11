using Lurp.Tests.Docs;

namespace Lurp.Tests;

public sealed class DocsInSyncTests
{
    [Fact]
    public void GeneratedSections_MatchTheCode()
    {
        var root = TestRepoRoot.Find();
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
            "Regenerate the docs, then review the diff of the docs." + Environment.NewLine +
            "  bash:       LURP_UPDATE_DOCS=1 dotnet test tests/Lurp.Tests.csproj --filter \"FullyQualifiedName~DocsInSyncTests\"" + Environment.NewLine +
            "  PowerShell: $env:LURP_UPDATE_DOCS = '1'; dotnet test tests/Lurp.Tests.csproj --filter \"FullyQualifiedName~DocsInSyncTests\"; Remove-Item Env:LURP_UPDATE_DOCS");
    }

    [Fact]
    public void EveryMarkerInTheDocs_HasARenderer()
    {
        var root = TestRepoRoot.Find();
        var known = DocCatalog.Sections
            .Select(section => (File: section.RelativePath, section.Key))
            .ToHashSet();

        var failures = new List<string>();
        var markerCount = 0;
        foreach (var relative in DocFiles.Markdown(root))
        {
            foreach (var key in DocSections.FindKeys(File.ReadAllText(Path.Combine(root, relative))))
            {
                markerCount++;
                if (!known.Contains((relative, key)))
                    failures.Add($"{relative} :: {key} :: no renderer");
            }
        }

        Assert.True(markerCount >= 3, $"Expected at least 3 generated markers, found {markerCount}.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
