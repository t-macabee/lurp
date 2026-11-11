using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lurp.Tests.Docs;

/// <summary>What a doc may cite: the repository files, the source identifiers and the listed exceptions.</summary>
internal sealed record DocReferenceContext(
    string Root,
    IReadOnlySet<string> Files,
    IReadOnlySet<string> Directories,
    ILookup<string, string> FilesByName,
    IReadOnlySet<string> Identifiers,
    IReadOnlySet<string> LurpFlags,
    IReadOnlySet<string> ScriptFlags,
    IReadOnlyDictionary<string, string> ExternalNames,
    IReadOnlyDictionary<(string File, string Flag), string> ExternalFlags);

/// <summary>
///     The reference checks of D107 for one Markdown text. Each check returns failures as
///     <c>file:line: text</c>. Code fences are skipped; an inline code span is one token.
/// </summary>
internal static partial class DocReferenceChecker
{
    private static readonly string[] ScriptExtensions =
        [".ps1", ".psm1", ".py", ".yml", ".yaml", ".sh", ".csproj", ".props", ".targets", ".json", ".slnx", ".sql", ".runsettings"];

    private static readonly SyntaxKind[] StringTokenKinds =
    [
        SyntaxKind.StringLiteralToken, SyntaxKind.InterpolatedStringTextToken,
        SyntaxKind.SingleLineRawStringLiteralToken, SyntaxKind.MultiLineRawStringLiteralToken,
        SyntaxKind.Utf8StringLiteralToken,
    ];

    [GeneratedRegex("`(?<span>[^`\\n]+)`")]
    private static partial Regex InlineSpanPattern();

    [GeneratedRegex(@"!?\[(?<text>[^\]]*)\]\((?<target>[^)\s]+)\)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*:")]
    private static partial Regex SchemePattern();

    [GeneratedRegex(@"^#{1,6}\s+(?<text>.*?)\s*#*\s*$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagPattern();

    [GeneratedRegex(@"[^\p{L}\p{Nd}\p{Mn}\p{Pc} \-]")]
    private static partial Regex SlugDropPattern();

    [GeneratedRegex(@"^(?<path>(?:[\w.\-]+/)+[\w.\-]+\.(?:cs|ps1|psm1|py|yml|yaml|sh|md|json|csproj|props|targets|slnx|html|txt|sql|runsettings))(?::(?<from>\d+)(?:-(?<to>\d+))?)?$")]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^(?<name>[\w\-][\w.\-]*\.(?:cs|ps1|psm1|py|yml|yaml|sh|csproj|props))(?::(?<from>\d+)(?:-(?<to>\d+))?)?$")]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(@"^[A-Z][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$|^[A-Z][a-z]+[A-Z][A-Za-z0-9_]*$|^[A-Z][A-Za-z0-9]*_[A-Za-z0-9_]+$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"(?<![\w\-])--[a-z][a-z0-9\-]*")]
    private static partial Regex FlagPattern();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex WordPattern();

    // ---- Markdown reading ----

    /// <summary>The lines outside code fences, 1-based.</summary>
    internal static IEnumerable<(int Line, string Text)> ProseLines(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inFence = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence)
                yield return (index + 1, lines[index]);
        }
    }

    /// <summary>The inline code spans outside code fences, trimmed.</summary>
    internal static IEnumerable<(int Line, string Span)> InlineSpans(string markdown)
    {
        foreach (var (line, text) in ProseLines(markdown))
            foreach (Match match in InlineSpanPattern().Matches(text))
                yield return (line, match.Groups["span"].Value.Trim());
    }

    // ---- Check 1: links and anchors ----

    /// <summary>The GitHub heading anchors of a Markdown text, with the -1, -2 suffixes of repeated headings.</summary>
    internal static IReadOnlySet<string> Anchors(string markdown)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, text) in ProseLines(markdown))
        {
            var match = HeadingPattern().Match(text);
            if (!match.Success)
                continue;
            var slug = Slug(match.Groups["text"].Value);
            var seen = counts.GetValueOrDefault(slug);
            counts[slug] = seen + 1;
            anchors.Add(seen == 0 ? slug : $"{slug}-{seen}");
        }

        return anchors;
    }

    /// <summary>
    ///     The GitHub Flavored Markdown anchor of a heading: the rendered text in lower case,
    ///     without punctuation except '-' and '_', with each space replaced by '-'.
    /// </summary>
    internal static string Slug(string heading)
    {
        var text = HtmlTagPattern().Replace(heading, "");
        text = LinkPattern().Replace(text, match => match.Groups["text"].Value);
        text = text.Replace("`", "", StringComparison.Ordinal).Replace("*", "", StringComparison.Ordinal);
        text = SlugDropPattern().Replace(text.Trim().ToLowerInvariant(), "");
        return text.Replace(' ', '-');
    }

    internal static IReadOnlyList<string> CheckLinks(string file, string markdown, DocReferenceContext context, Func<string, string> readDoc)
    {
        var failures = new List<string>();
        var directory = Path.GetDirectoryName(file)?.Replace('\\', '/') ?? "";
        foreach (var (line, text) in ProseLines(markdown))
        {
            var prose = InlineSpanPattern().Replace(text, "");
            foreach (Match match in LinkPattern().Matches(prose))
            {
                var target = match.Groups["target"].Value;
                if (SchemePattern().IsMatch(target))
                    continue;

                var hash = target.IndexOf('#');
                var pathPart = hash < 0 ? target : target[..hash];
                var fragment = hash < 0 ? "" : target[(hash + 1)..];
                var resolved = pathPart.Length == 0 ? file : Combine(directory, pathPart);

                if (resolved is null || !(context.Files.Contains(resolved) || context.Directories.Contains(resolved)))
                {
                    failures.Add($"{file}:{line}: link target '{target}' does not exist");
                    continue;
                }

                if (fragment.Length == 0 || !resolved.EndsWith(".md", StringComparison.Ordinal))
                    continue;
                if (!Anchors(readDoc(resolved)).Contains(fragment))
                    failures.Add($"{file}:{line}: anchor '#{fragment}' does not exist in {resolved}");
            }
        }

        return failures;
    }

    // ---- Check 2: cited paths and names ----

    internal static IReadOnlyList<string> CheckNames(string file, string markdown, DocReferenceContext context, ISet<string> usedExternalNames)
    {
        var failures = new List<string>();
        var directory = Path.GetDirectoryName(file)?.Replace('\\', '/') ?? "";
        foreach (var (line, raw) in InlineSpans(markdown))
        {
            var span = raw.EndsWith("()", StringComparison.Ordinal) ? raw[..^2] : raw;
            if (context.ExternalNames.ContainsKey(span))
            {
                usedExternalNames.Add(span);
                continue;
            }

            var path = PathPattern().Match(span);
            if (path.Success)
            {
                var cited = path.Groups["path"].Value;
                var resolved = context.Files.Contains(cited) ? cited : Combine(directory, cited);
                if (resolved is null || !context.Files.Contains(resolved))
                    failures.Add($"{file}:{line}: path '{cited}' does not exist");
                else
                    failures.AddRange(CheckLine(file, line, resolved, path, context));
                continue;
            }

            var name = FileNamePattern().Match(span);
            if (name.Success)
            {
                var matches = context.FilesByName[name.Groups["name"].Value].ToList();
                if (matches.Count == 0)
                    failures.Add($"{file}:{line}: file '{name.Groups["name"].Value}' does not exist");
                else if (name.Groups["from"].Success && matches.Count > 1)
                    failures.Add($"{file}:{line}: '{span}' cites a line of a file name that {matches.Count} files have; cite the path");
                else if (name.Groups["from"].Success)
                    failures.AddRange(CheckLine(file, line, matches[0], name, context));
                continue;
            }

            if (!IdentifierPattern().IsMatch(span))
                continue;
            var missing = span.Split('.').Where(segment => !context.Identifiers.Contains(segment)).ToList();
            if (missing.Count > 0)
                failures.Add($"{file}:{line}: '{span}' names {string.Join(", ", missing.Select(segment => $"'{segment}'"))}, which no source file declares or uses; if it names another repository or tool, list it in tests/Docs/external-names.txt");
        }

        return failures;
    }

    private static IEnumerable<string> CheckLine(string file, int line, string resolved, Match match, DocReferenceContext context)
    {
        if (!match.Groups["from"].Success)
            yield break;
        var count = File.ReadLines(Path.Combine(context.Root, resolved)).Count();
        var last = int.Parse(match.Groups[match.Groups["to"].Success ? "to" : "from"].Value, CultureInfo.InvariantCulture);
        if (last > count)
            yield return $"{file}:{line}: line {last} is past the end of {resolved} ({count} lines)";
    }

    // ---- Check 3: flags ----

    internal static IReadOnlyList<string> CheckFlags(string file, string markdown, DocReferenceContext context, ISet<(string File, string Flag)> usedExternalFlags)
    {
        var failures = new List<string>();
        foreach (var (line, span) in InlineSpans(markdown))
        {
            foreach (Match match in FlagPattern().Matches(span))
            {
                var flag = match.Value;
                if (context.LurpFlags.Contains(flag) || context.ScriptFlags.Contains(flag))
                    continue;
                if (context.ExternalFlags.ContainsKey((file, flag)))
                {
                    usedExternalFlags.Add((file, flag));
                    continue;
                }

                failures.Add($"{file}:{line}: '{flag}' is not a flag of any mode; if it is a flag of another tool, list it in tests/Docs/external-flags.txt");
            }
        }

        return failures;
    }

    // ---- Context ----

    internal static DocReferenceContext Load(string root)
    {
        var files = DocFiles.Repository(root);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in files)
            for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                directories.Add(parent.Replace('\\', '/'));

        return new DocReferenceContext(
            root,
            files.ToHashSet(StringComparer.Ordinal),
            directories,
            files.ToLookup(path => Path.GetFileName(path), StringComparer.Ordinal),
            Identifiers(root, files),
            LurpFlags(),
            ScriptFlags(root, files),
            ReadExternalNames(Path.Combine(root, "tests", "Docs", "external-names.txt")),
            ReadExternalFlags(Path.Combine(root, "tests", "Docs", "external-flags.txt")));
    }

    /// <summary>
    ///     Adds the identifier tokens and the words of the string literals of one C# source.
    ///     A comment does not count, so a stale name in a comment cannot hide a stale name in a doc (D123).
    /// </summary>
    internal static void AddCSharpNames(string source, ISet<string> names)
    {
        foreach (var token in CSharpSyntaxTree.ParseText(source).GetRoot().DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.IdentifierToken))
                names.Add(token.ValueText);
            else if (StringTokenKinds.Contains(token.Kind()))
                foreach (Match match in WordPattern().Matches(token.ValueText))
                    names.Add(match.Value);
        }
    }

    /// <summary>The names of the C# sources (<see cref="AddCSharpNames" />) and the words of the script and project files.</summary>
    private static HashSet<string> Identifiers(string root, IEnumerable<string> files)
    {
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in files)
        {
            var extension = Path.GetExtension(path);
            if (extension == ".cs")
            {
                AddCSharpNames(File.ReadAllText(Path.Combine(root, path)), identifiers);
            }
            else if (ScriptExtensions.Contains(extension, StringComparer.Ordinal))
            {
                foreach (Match match in WordPattern().Matches(File.ReadAllText(Path.Combine(root, path))))
                    identifiers.Add(match.Value);
            }
        }

        return identifiers;
    }

    /// <summary>
    ///     The flags that the scripts and workflows use (files under <c>scripts/</c> and
    ///     <c>.github/</c>): the harness, the generators, <c>dotnet</c> and <c>gh</c> (D123).
    /// </summary>
    private static HashSet<string> ScriptFlags(string root, IEnumerable<string> files)
    {
        var flags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in files)
        {
            if (!path.StartsWith("scripts/", StringComparison.Ordinal) && !path.StartsWith(".github/", StringComparison.Ordinal))
                continue;
            if (Path.GetExtension(path) is not (".cs" or ".ps1" or ".psm1" or ".py" or ".yml" or ".yaml" or ".sh"))
                continue;
            foreach (Match match in FlagPattern().Matches(File.ReadAllText(Path.Combine(root, path))))
                flags.Add(match.Value);
        }

        return flags;
    }

    /// <summary>Every flag name of every mode and the global flags, without the trailing '='.</summary>
    private static HashSet<string> LurpFlags() =>
        Program.ModeRegistry.SelectMany(entry => entry.Specs).Select(spec => spec.Name)
            .Concat(CliFlagValidation.GlobalFlags)
            .Select(name => name.TrimEnd('='))
            .Where(name => name.StartsWith("--", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> ReadExternalNames(string path) =>
        ReadList(path, 2).ToDictionary(fields => fields[0], fields => fields[1], StringComparer.Ordinal);

    private static Dictionary<(string File, string Flag), string> ReadExternalFlags(string path) =>
        ReadList(path, 3).ToDictionary(fields => (fields[0], fields[1]), fields => fields[2]);

    /// <summary>Lines of <c>field | field | reason</c>; '#' starts a comment line. Each field must be non-empty.</summary>
    private static IEnumerable<string[]> ReadList(string path, int fieldCount)
    {
        if (!File.Exists(path))
            yield break;
        var number = 0;
        foreach (var raw in File.ReadLines(path))
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var fields = line.Split('|').Select(field => field.Trim()).ToArray();
            if (fields.Length != fieldCount || fields.Any(field => field.Length == 0))
                throw new InvalidOperationException(
                    $"{path}:{number}: expected {fieldCount} non-empty fields separated by '|', the last one the reason.");
            yield return fields;
        }
    }

    /// <summary>Joins a doc-relative path to the doc's folder; null when it leaves the repository.</summary>
    private static string? Combine(string directory, string relative)
    {
        var parts = new List<string>(directory.Length == 0 ? [] : directory.Split('/'));
        foreach (var part in relative.TrimEnd('/').Split('/'))
        {
            if (part is "" or ".")
                continue;
            if (part == "..")
            {
                if (parts.Count == 0)
                    return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        return string.Join('/', parts);
    }
}
