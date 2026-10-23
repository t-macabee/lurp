using System.Text.RegularExpressions;
using Lurp.Handlers;

namespace Lurp.Tests.Docs;

internal enum DocExampleKind { Command, Usage }

internal sealed record DocExample(
    string File,
    int Line,
    string Text,
    DocExampleKind Kind,
    string? SkipReason,
    string? MarkerProblem);

/// <summary>
///     Extracts the <c>lurp …</c> commands and <c>--mode=…</c> usage lines the
///     documentation shows, and parses each one against <see cref="Program.ModeRegistry" />.
///     A stale flag, an unknown mode, or a value outside a flag's allowed set fails here
///     rather than in a reader's shell.
/// </summary>
internal static partial class DocExamples
{
    /// <summary>The files the extractor reads, relative to the repo root.</summary>
    internal static readonly string[] Files =
    [
        "README.md",
        "docs/CLI_REFERENCE.md",
        "docs/ARCHITECTURE.md",
        "CONTRIBUTING.md",
        "VERSIONING.md",
        "SECURITY.md",
        "docs/RELEASE_NOTES_2.0.0.md",
        "docs/RELEASE_NOTES_3.0.0.md",
        "docs/RELEASE_NOTES_3.0.1.md",
        "docs/RELEASE_NOTES_3.0.2.md",
        "docs/RELEASE_NOTES_3.5.0.md",
        "docs/RELEASE_NOTES_3.5.1.md",
        "docs/RELEASE_NOTES_3.5.2.md",
        "docs/RELEASE_NOTES_3.6.0.md",
        "docs/RELEASE_NOTES_3.6.1.md",
        "notes/TRUST_KERNEL.md",
    ];

    private static readonly string[] CommandFences = ["", "bash", "sh", "shell", "powershell"];

    [GeneratedRegex(@"<!--\s*doc-test:\s*skip\s*,(?<reason>.*?)-->")]
    private static partial Regex SkipMarkerPattern();

    [GeneratedRegex("`(?<span>[^`\\n]+)`")]
    private static partial Regex InlineSpanPattern();

    /// <summary>Reads every file in <see cref="Files" /> and extracts its examples, in file order.</summary>
    internal static IReadOnlyList<DocExample> ExtractAll(string repoRoot)
    {
        var examples = new List<DocExample>();
        foreach (var file in Files)
        {
            var path = Path.Combine(repoRoot, file.Replace('/', Path.DirectorySeparatorChar));
            examples.AddRange(Extract(File.ReadAllText(path), file));
        }

        return examples;
    }

    /// <summary>Splits a command line on spaces and drops a leading <c>lurp</c>.</summary>
    internal static List<string> Tokens(string text)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count > 0 && tokens[0] == "lurp")
            tokens.RemoveAt(0);
        return tokens;
    }

    /// <summary>The value of the first <c>--mode=</c> token, or null.</summary>
    internal static string? ModeOf(IReadOnlyList<string> tokens) =>
        tokens.FirstOrDefault(token => token.StartsWith("--mode=", StringComparison.Ordinal))?["--mode=".Length..];

    internal static IReadOnlyList<DocExample> Extract(string markdown, string file)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var examples = new List<DocExample>();
        var inFence = false;
        var commandFence = false;
        var fenceStart = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            var trimmed = raw.Trim();
            var lineNumber = index + 1;

            if (IsFenceMarker(trimmed))
            {
                if (inFence)
                {
                    inFence = false;
                    commandFence = false;
                }
                else
                {
                    inFence = true;
                    commandFence = CommandFences.Contains(FenceInfo(trimmed), StringComparer.OrdinalIgnoreCase);
                    fenceStart = lineNumber;
                }

                continue;
            }

            if (!inFence)
            {
                foreach (var span in InlineSpans(raw))
                {
                    var text = span.Trim();
                    if (!text.StartsWith("lurp --mode=", StringComparison.Ordinal))
                        continue;
                    var (reason, problem) = FindMarker(raw, lineNumber, file);
                    if (reason is null && problem is null)
                        (reason, problem) = FindMarkerAbove(lines, lineNumber, file);
                    examples.Add(new DocExample(file, lineNumber, text, DocExampleKind.Command, reason, problem));
                }

                continue;
            }

            if (!commandFence)
                continue;

            var (lineReason, lineProblem) = FindMarker(raw, lineNumber, file);
            var content = DropTrailingComment(RemoveMarker(trimmed));

            DocExampleKind? kind = null;
            if (content.StartsWith("lurp ", StringComparison.Ordinal))
                kind = DocExampleKind.Command;
            else if (content.StartsWith("--mode=", StringComparison.Ordinal))
                kind = DocExampleKind.Usage;
            if (kind is null)
                continue;

            var fenceReason = lineReason;
            var fenceProblem = lineProblem;
            if (fenceReason is null && fenceProblem is null)
            {
                (fenceReason, fenceProblem) = FindMarkerAbove(lines, lineNumber, file);
                if (fenceReason is null && fenceProblem is null)
                    (fenceReason, fenceProblem) = FindMarkerAbove(lines, fenceStart, file);
            }

            examples.Add(new DocExample(file, lineNumber, content, kind.Value, fenceReason, fenceProblem));
        }

        return examples;
    }

    /// <summary>Tier 1: parses one example against the registry. Empty means it passes.</summary>
    internal static IReadOnlyList<string> Validate(DocExample example)
    {
        var failures = new List<string>();
        var header = $"{example.File}:{example.Line}";

        var text = example.Kind == DocExampleKind.Usage
            ? example.Text.Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal)
            : example.Text;

        var tokens = Tokens(text);

        foreach (var token in tokens)
            if (token.IndexOfAny(['"', '\'']) >= 0)
                failures.Add($"{header}: unsupported quoting in '{token}'");

        var mode = ModeOf(tokens);
        if (mode is null)
        {
            if (tokens.Contains("--version") || tokens.Contains("--help") || tokens.Contains("-h"))
                return failures;
            failures.Add($"{header}: no --mode= in '{example.Text}'");
            return failures;
        }

        var entry = Program.FindMode(mode);
        if (entry is null)
        {
            failures.Add($"{header}: unknown mode '{mode}'");
            return failures;
        }

        try
        {
            CliFlagValidation.Validate(entry, [.. tokens]);
        }
        catch (CliExitException ex)
        {
            failures.Add($"{header}: {ex.Message}");
        }

        foreach (var token in tokens)
        {
            if (!token.StartsWith("--", StringComparison.Ordinal))
                continue;
            var separator = token.IndexOf('=');
            if (separator < 0)
                continue;
            var name = token[..(separator + 1)];
            var value = token[(separator + 1)..];
            var spec = entry.Specs.FirstOrDefault(candidate => candidate.Name == name);
            if (spec?.AllowedValues is not { } allowed)
                continue;
            if (value.StartsWith('<'))
                continue;
            foreach (var part in value.Split(','))
                if (!allowed.Any(a => string.Equals(a, part, StringComparison.OrdinalIgnoreCase)))
                    failures.Add($"{header}: '{value}' is not an allowed value for {name} (valid: {string.Join(", ", allowed)})");
        }

        foreach (var spec in entry.Specs)
        {
            if (spec.Requirement != FlagRequirement.Required || spec.EnvironmentVariable is not null)
                continue;
            var present = tokens.Any(token => spec.TakesValue
                ? token.StartsWith(spec.Name, StringComparison.Ordinal)
                : token == spec.Name);
            if (!present)
                failures.Add($"{header}: required flag {spec.Name} is missing");
        }

        return failures;
    }

    private static bool IsFenceMarker(string trimmed) =>
        trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);

    private static string FenceInfo(string trimmed) => trimmed.TrimStart('`', '~').Trim();

    private static IEnumerable<string> InlineSpans(string line)
    {
        foreach (Match match in InlineSpanPattern().Matches(line))
            yield return match.Groups["span"].Value;
    }

    private static string RemoveMarker(string line) => SkipMarkerPattern().Replace(line, "").Trim();

    private static string DropTrailingComment(string line)
    {
        var comment = line.IndexOf(" #", StringComparison.Ordinal);
        return (comment < 0 ? line : line[..comment]).TrimEnd();
    }

    private static (string? Reason, string? Problem) FindMarker(string line, int lineNumber, string file)
    {
        var match = SkipMarkerPattern().Match(line);
        if (!match.Success)
            return (null, null);
        var reason = match.Groups["reason"].Value.Trim();
        return reason.Length == 0
            ? (null, $"{file}:{lineNumber}: skip marker has a blank reason")
            : (reason, null);
    }

    private static (string? Reason, string? Problem) FindMarkerAbove(string[] lines, int lineNumber, string file)
    {
        for (var index = lineNumber - 2; index >= 0; index--)
        {
            if (lines[index].Trim().Length == 0)
                continue;
            return FindMarker(lines[index], index + 1, file);
        }

        return (null, null);
    }
}
