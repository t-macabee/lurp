using System.Globalization;
using System.Text.Json;
using Lurp.Tests.Docs;
using Lurp.Workspace;

namespace Lurp.Tests;

/// <summary>
///     DC-28 / S6-F15 item 11: <c>docs/CLI_REFERENCE.md</c> under <c>### Freshness</c> lists
///     the keys and order of the read <c>freshness</c> block, the <c>lurp_status</c> block,
///     and mismatch rows; the table is the contract checked against <see cref="FreshnessJson" />.
/// </summary>
public sealed class FreshnessKeyTableTests
{
    private const string Doc = "docs/CLI_REFERENCE.md";
    private const string Heading = "### Freshness";

    internal static (string[] ReadKeys, string[] StatusKeys, string[] RowKeys) ReadTableKeys()
    {
        var markdown = File.ReadAllText(Path.Combine(TestRepoRoot.Find(), Doc));
        var inSection = false;
        var tableRows = new List<string[]>();

        foreach (var (_, text) in DocReferenceChecker.ProseLines(markdown))
        {
            if (text.StartsWith('#'))
            {
                if (inSection)
                    break;
                inSection = text.Trim() == Heading;
                continue;
            }

            if (!inSection)
                continue;

            var trimmed = text.Trim();
            if (trimmed.StartsWith('|'))
            {
                var cells = trimmed.Trim('|').Split('|').Select(static c => c.Trim()).ToArray();
                tableRows.Add(cells);
            }
            else if (tableRows.Count > 0)
            {
                break;
            }
        }

        Assert.True(tableRows.Count >= 3, $"{Doc} under '{Heading}' must contain a markdown key table.");

        var readEntries = new List<(int Order, string Key)>();
        var statusEntries = new List<(int Order, string Key)>();
        var rowEntries = new List<(int Order, string Key)>();

        foreach (var cells in tableRows.Skip(2))
        {
            Assert.True(cells.Length >= 4, $"Unexpected row shape in {Doc} Freshness table.");
            var key = cells[0].Trim('`');
            if (int.TryParse(cells[1], CultureInfo.InvariantCulture, out var readOrder))
                readEntries.Add((readOrder, key));
            if (int.TryParse(cells[2], CultureInfo.InvariantCulture, out var statusOrder))
                statusEntries.Add((statusOrder, key));
            if (int.TryParse(cells[3], CultureInfo.InvariantCulture, out var rowOrder))
                rowEntries.Add((rowOrder, key));
        }

        return (
            readEntries.OrderBy(static e => e.Order).Select(static e => e.Key).ToArray(),
            statusEntries.OrderBy(static e => e.Order).Select(static e => e.Key).ToArray(),
            rowEntries.OrderBy(static e => e.Order).Select(static e => e.Key).ToArray());
    }

    [Fact]
    public void TheKeyTable_ListsTheKeysOfBothBlocks()
    {
        var (readKeys, statusKeys, rowKeys) = ReadTableKeys();
        var stamp = new FreshnessStamp("fresh", "stat", 0, [], DateTime.UtcNow, "snap-1", "documents_only");
        var full = new WorkspaceFreshness.FreshnessResult(
            false,
            [new SnapshotMismatch(MismatchKind.DocumentModified, "desc", new DocumentId("A.cs"), "detail")]);

        Assert.Equal(readKeys, KeyNames(FreshnessJson.Cheap(stamp, 50)));
        Assert.Equal(statusKeys, KeyNames(FreshnessJson.ForStatus(stamp, 50)));
        Assert.Equal(statusKeys, KeyNames(FreshnessJson.ForStatus("snap-1", full, DateTime.UtcNow, 50, 50, true)));

        var capped = FreshnessJson.CapMismatches(full.Mismatches, 50);
        Assert.Equal(rowKeys, KeyNames(capped.Rows[0]));
    }

    private static string[] KeyNames(object value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return doc.RootElement.EnumerateObject().Select(static p => p.Name).ToArray();
    }
}
