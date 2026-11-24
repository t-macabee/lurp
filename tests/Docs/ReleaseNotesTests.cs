using System.Globalization;
using System.Text.RegularExpressions;
using Lurp.Storage;
using Lurp.Tests.Docs;
using Lurp.Workspace;

namespace Lurp.Tests;

/// <summary>
///     D104: the release notes are part of the version contract. Each notes file has a "Versions"
///     table; the tables form one chain; the newest table states the current constants; a contract
///     change has its heading; README links every file and installs the current tool version.
///     A notes file newer than the tool version is the draft of the next release (D121).
/// </summary>
public sealed partial class ReleaseNotesTests
{
    private const string Tool = "Tool";
    private const string Contract = "CLI/MCP contract";
    private const string OutputSchema = "Output schema";
    private const string DatabaseSchema = "Database schema";
    private const string Extractor = "Extractor";
    private static readonly string[] Rows = [Tool, Contract, OutputSchema, DatabaseSchema, Extractor];

    [GeneratedRegex(@"^RELEASE_NOTES_(?<version>\d+\.\d+\.\d+)\.md$")]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(@"\]\([^)\s]*RELEASE_NOTES_\d+\.\d+\.\d+\.md\)")]
    private static partial Regex NotesLinkPattern();

    [GeneratedRegex(@"^#{1,6}\s+.*\(CLI/MCP contract version (?<n>\d+)\)\s*$", RegexOptions.Multiline)]
    private static partial Regex ContractHeadingPattern();

    [Fact]
    public void TheNotesOfTheToolVersion_Exist()
    {
        var notes = Notes();
        Assert.Contains(notes, file => file.Version == Version.Parse(VersionConstants.ToolVersion));
    }

    [Fact]
    public void AtMostOneNotesFile_IsNewerThanTheToolVersion()
    {
        var drafts = Notes().Where(file => file.Version > Version.Parse(VersionConstants.ToolVersion)).ToList();
        Assert.True(drafts.Count <= 1, "More than one draft: " + string.Join(", ", drafts.Select(file => file.Name)));
    }

    [Fact]
    public void EveryNotesTitle_SaysDraftOnlyAboveTheToolVersion()
    {
        var toolVersion = Version.Parse(VersionConstants.ToolVersion);
        var failures = new List<string>();
        foreach (var file in Notes())
        {
            var suffix = file.Version > toolVersion ? " (draft)" : "";
            var expected = $"# Lurp {file.Version.ToString(3)}{suffix}";
            var actual = file.Text.Split('\n', 2)[0].TrimEnd('\r');
            if (actual != expected)
                failures.Add($"{file.Name}: expected '{expected}', found '{actual}'");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void EveryNotesFile_HasAVersionsTable_ThatContinuesTheFileBefore()
    {
        var failures = new List<string>();
        (string Name, IReadOnlyDictionary<string, (string From, string To)> Table)? previous = null;
        foreach (var file in Notes())
        {
            var table = VersionsTable(file.Text);
            var missing = Rows.Where(row => !table.ContainsKey(row)).ToList();
            if (missing.Count > 0)
            {
                failures.Add($"{file.Name}: the \"Versions\" table has no row {string.Join(", ", missing)}");
                previous = null;
                continue;
            }

            if (table[Tool].To != file.Version.ToString(3))
                failures.Add($"{file.Name}: the Tool row ends with {table[Tool].To}, not {file.Version.ToString(3)}");
            if (previous is { } before)
                foreach (var row in Rows)
                    if (table[row].From != before.Table[row].To)
                        failures.Add($"{file.Name}: {row} starts at {table[row].From}, but {before.Name} ends at {before.Table[row].To}");
            previous = (file.Name, table);
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void TheNewestVersionsTable_StatesTheConstants()
    {
        var newest = Notes()[^1];
        var table = VersionsTable(newest.Text);
        var expected = new Dictionary<string, string>
        {
            [Contract] = VersionConstants.CliMcpContractVersion.ToString(CultureInfo.InvariantCulture),
            [OutputSchema] = VersionConstants.OutputSchemaVersion.ToString(CultureInfo.InvariantCulture),
            [DatabaseSchema] = MigrationRunner.LatestVersion.ToString(CultureInfo.InvariantCulture),
            [Extractor] = VersionConstants.ExtractorVersion,
        };

        var failures = expected
            .Where(pair => !table.TryGetValue(pair.Key, out var value) || value.To != pair.Value)
            .Select(pair => $"{newest.Name}: {pair.Key} must end with {pair.Value}, found {(table.TryGetValue(pair.Key, out var value) ? value.To : "no row")}")
            .ToList();
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void EveryContractChange_HasAHeadingWithTheNewVersion()
    {
        var failures = new List<string>();
        foreach (var file in Notes())
        {
            var table = VersionsTable(file.Text);
            if (!table.TryGetValue(Contract, out var contract) || contract.From == contract.To)
                continue;
            var headings = ContractHeadingPattern().Matches(file.Text).Select(match => match.Groups["n"].Value);
            if (!headings.Contains(contract.To))
                failures.Add($"{file.Name}: the contract moves to {contract.To}, and no heading ends with \"(CLI/MCP contract version {contract.To})\"");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Readme_LinksEveryNotesFile_AndInstallsTheToolVersion()
    {
        var readme = File.ReadAllText(Path.Combine(TestRepoRoot.Find(), "README.md"));
        var failures = Notes()
            .Where(file => !readme.Contains($"](docs/{file.Name})", StringComparison.Ordinal))
            .Select(file => $"README.md does not link docs/{file.Name}")
            .ToList();
        if (!readme.Contains($"--version {VersionConstants.ToolVersion}", StringComparison.Ordinal))
            failures.Add($"README.md does not contain '--version {VersionConstants.ToolVersion}'");

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void EveryReadmeEntry_SaysDraftOnlyAboveTheToolVersion()
    {
        var toolVersion = Version.Parse(VersionConstants.ToolVersion);
        var readme = File.ReadAllText(Path.Combine(TestRepoRoot.Find(), "README.md"));
        var failures = new List<string>();
        foreach (var file in Notes())
        {
            var entry = Regex.Match(readme, $@"\]\(docs/{Regex.Escape(file.Name)}\)(?<text>[^\r\n]*)").Groups["text"].Value;
            if (file.Version > toolVersion)
            {
                if (entry != " (draft)")
                    failures.Add($"README.md: the {file.Name} entry must be the link and ' (draft)', found '{entry}'");
            }
            else if (!Regex.IsMatch(entry, @"^: \S") || entry.Contains("(draft)", StringComparison.Ordinal))
            {
                failures.Add($"README.md: the {file.Name} entry must be the link, ': ' and a summary without '(draft)', found '{entry}'");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void OnlyReadme_ListsTheNotes()
    {
        var root = TestRepoRoot.Find();
        var failures = DocFiles.Live(root)
            .Where(file => file != "README.md")
            .Select(file => (File: file, Links: NotesLinkPattern().Matches(File.ReadAllText(Path.Combine(root, file))).Select(match => match.Value).Distinct().Count()))
            .Where(item => item.Links > 1)
            .Select(item => $"{item.File} links {item.Links} release-notes files; link the README list instead (D108)")
            .ToList();

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>The notes files in version order.</summary>
    private static List<(string Name, Version Version, string Text)> Notes()
    {
        var docs = Path.Combine(TestRepoRoot.Find(), "docs");
        return Directory.EnumerateFiles(docs, "RELEASE_NOTES_*.md")
            .Select(path => (Path: path, Match: FileNamePattern().Match(Path.GetFileName(path))))
            .Select(item => item.Match.Success
                ? (Name: Path.GetFileName(item.Path), Version: Version.Parse(item.Match.Groups["version"].Value), Text: File.ReadAllText(item.Path))
                : throw new InvalidOperationException($"{item.Path}: a notes file is named RELEASE_NOTES_<major>.<minor>.<patch>.md"))
            .OrderBy(file => file.Version)
            .ToList();
    }

    /// <summary>
    ///     The five rows of the "Versions" table: the second cell and the last cell, without
    ///     backticks and without a leading 'v'. A row label matches by prefix ("Output schema
    ///     (capsule JSON)" is the output schema row); "Extractor" matches only itself.
    /// </summary>
    private static Dictionary<string, (string From, string To)> VersionsTable(string text)
    {
        var table = new Dictionary<string, (string From, string To)>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, line => line.Trim() == "## Versions");
        if (start < 0)
            return table;

        foreach (var line in lines.Skip(start + 1).SkipWhile(line => line.Trim().Length == 0).TakeWhile(line => line.TrimStart().StartsWith('|')))
        {
            var cells = line.Trim().Trim('|').Split('|').Select(Clean).ToArray();
            if (cells.Length < 3)
                continue;
            var row = Rows.FirstOrDefault(name => name == Extractor
                ? cells[0] == Extractor
                : cells[0].StartsWith(name, StringComparison.Ordinal));
            if (row is not null)
                table[row] = (cells[1], cells[^1]);
        }

        return table;
    }

    private static string Clean(string cell)
    {
        var value = cell.Trim().Trim('`');
        return value.Length > 1 && value[0] == 'v' && char.IsDigit(value[1]) ? value[1..] : value;
    }
}
