using System.Collections.ObjectModel;
using System.Text.Json;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class CliOutputWriter
{
    internal static readonly IReadOnlyDictionary<string, string> JsonlItemTypes =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["search"] = "result",
            ["grep"] = "result",
            ["find-symbol"] = "symbol",
            ["impact"] = "symbol",
            ["outline"] = "declaration",
            ["diagnostics"] = "diagnostic",
            ["get-annotations"] = "annotation",
            ["dead-candidates"] = "candidate",
            ["context"] = "item"
        });

    public static void Write<TItem>(OutputMode mode, IOutputSink output, Action writeSummary,
        Func<object> buildJson, Func<object> buildMeta, string itemRecordType, IEnumerable<TItem> items)
    {
        switch (mode)
        {
            case OutputMode.Summary:
                writeSummary();
                break;
            case OutputMode.Jsonl:
                output.WriteLine(JsonSerializer.Serialize(new { type = "meta", meta = buildMeta() }, HandlerBootstrap.CompactJson));
                foreach (var item in items)
                {
                    var record = new Dictionary<string, object?> { ["type"] = itemRecordType, [itemRecordType] = item };
                    output.WriteLine(JsonSerializer.Serialize(record, HandlerBootstrap.CompactJson));
                }
                break;
            default:
                output.WriteLine(JsonSerializer.Serialize(buildJson(), HandlerBootstrap.IndentedJson));
                break;
        }
    }
}
