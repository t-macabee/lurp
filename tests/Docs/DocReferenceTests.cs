using Lurp.Tests.Docs;

namespace Lurp.Tests;

/// <summary>
///     D107: the prose of the docs names only things that exist. Links and anchors resolve in every
///     doc. Cited paths, names and flags resolve in the live docs; the release notes are history
///     (D109), so only their links are checked.
/// </summary>
public sealed class DocReferenceTests
{
    private static readonly Lazy<DocReferenceContext> Context = new(() => DocReferenceChecker.Load(TestRepoRoot.Find()));

    [Fact]
    public void EveryRelativeLinkAndAnchor_Resolves()
    {
        var context = Context.Value;
        var failures = new List<string>();
        foreach (var file in DocFiles.Markdown(context.Root))
            failures.AddRange(DocReferenceChecker.CheckLinks(file, Read(context, file), context, path => Read(context, path)));

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void EveryCitedPathAndName_Exists()
    {
        var context = Context.Value;
        var used = new HashSet<string>(StringComparer.Ordinal);
        var failures = new List<string>();
        foreach (var file in DocFiles.Live(context.Root))
            failures.AddRange(DocReferenceChecker.CheckNames(file, Read(context, file), context, used));
        failures.AddRange(context.ExternalNames.Keys.Where(name => !used.Contains(name))
            .Select(name => $"tests/Docs/external-names.txt: '{name}' is cited by no live doc; remove the line"));

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void EveryCitedFlag_IsALurpFlagOrListed()
    {
        var context = Context.Value;
        var used = new HashSet<(string File, string Flag)>();
        var failures = new List<string>();
        foreach (var file in DocFiles.Live(context.Root))
            failures.AddRange(DocReferenceChecker.CheckFlags(file, Read(context, file), context, used));
        failures.AddRange(context.ExternalFlags.Keys.Where(key => !used.Contains(key))
            .Select(key => $"tests/Docs/external-flags.txt: '{key.Flag}' is not cited in {key.File}; remove the line"));

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string Read(DocReferenceContext context, string file) =>
        File.ReadAllText(Path.Combine(context.Root, file));
}
