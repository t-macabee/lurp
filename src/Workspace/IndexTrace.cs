using System.Globalization;

namespace Lurp.Workspace;

/// <summary>
/// Temporary trace instrumentation for re-measuring per-stage syntax-tree
/// walks. Enabled by the LURP_INDEX_TRACE environment variable pointing at
/// a CSV output path. Scratch-only: never merged to main.
///
/// CSV columns: event,stage,pass,project,tree_path,detail
///
/// A tree_walk event is one (extraction stage, syntax tree) pair: a stage
/// beginning to walk the syntax of one tree. Events are deduplicated per
/// (pass, stage, detail, tree path) inside the writer, so the per-pass event
/// count equals the number of distinct such pairs.
/// </summary>
internal static class IndexTrace
{
    private static readonly string? Path;
    private static readonly bool Enabled;
    private static readonly object Lock = new();
    private static readonly HashSet<string> Seen = new(StringComparer.Ordinal);
    private static string? _pass;
    private static string? _project;

    static IndexTrace()
    {
        Path = Environment.GetEnvironmentVariable("LURP_INDEX_TRACE");
        Enabled = !string.IsNullOrEmpty(Path);
    }

    public static void BeginPass(string pass)
    {
        if (!Enabled)
            return;
        lock (Lock)
        {
            _pass = pass;
        }
        Write(pass, "pass_begin", "", "", "");
    }

    public static void SetProject(string project)
    {
        if (Enabled)
            _project = project;
    }

    /// <summary>
    /// Logs a tree-walk event for the current pass. Deduplicated per
    /// (pass, stage, detail, tree path).
    /// </summary>
    public static void TreeWalk(string stage, string detail, string treePath)
    {
        if (!Enabled)
            return;
        string? pass;
        lock (Lock)
        {
            pass = _pass;
            if (pass == null)
                return;
            if (!Seen.Add(string.Concat(pass, "\u0001", stage, "\u0001", detail, "\u0001", treePath)))
                return;
        }
        Write(pass, "tree_walk", stage, treePath, detail);
    }

    private static void Write(string pass, string evt, string stage, string treePath, string detail)
    {
        lock (Lock)
        {
            File.AppendAllText(
                Path!,
                string.Join(",",
                    evt,
                    Csv(stage),
                    Csv(pass),
                    Csv(_project ?? ""),
                    Csv(treePath),
                    Csv(detail)) + Environment.NewLine,
                System.Text.Encoding.UTF8);
        }
    }

    private static string Csv(string value)
        => value.IndexOf(',') < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";

    public static int DistinctEventCount()
    {
        lock (Lock)
        {
            return Seen.Count;
        }
    }

    public static void WriteSummary(string pass, string label)
    {
        if (!Enabled)
            return;
        Write(pass, "summary", label, "", DistinctEventCount().ToString(CultureInfo.InvariantCulture));
    }
}
