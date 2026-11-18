// Purpose: CLI help and unknown-mode error text.
// Owns: the human-readable mode/option reference printed by --help.
// Must not contain: dispatch logic or handler calls.

using Lurp.Workspace;

namespace Lurp;

internal static class HelpText
{
    private const int FlagColumn = 24;

    // The flags each mode accepts, and their prose, are read from Program.ModeRegistry[].Specs
    // (the FlagCatalog records) by PrintHelp, so the help inventory can never drift from the
    // validator or from the docs.

    // Trailing behavioural notes that are NOT per-flag (so they cannot cause flag
    // drift). Keyed by mode name; printed after that mode's flag list.
    private static readonly Dictionary<string, string> ModeNotes = new(StringComparer.Ordinal)
    {
        ["impact"] = "The response lists reached symbols, each with its depth, `shortest_path_count`, `frontier` flag and a deterministic witness path. It also carries `groups`: the symbols grouped by the first hop of their witness path, computed over ALL symbols before the page is cut, so the fan-out summary stays complete even when the symbol list is truncated. "
                     + "Note: a static call site emits both a `Calls` edge and a `StaticallyCalls` edge, and `shortest_path_count` counts both as separate routes; count distinct call sites (first_hop_source_symbol_id) rather than total edge rows when measuring fan-in.",
        ["context"] = "The capsule is always written to <output-dir>/capsule-<symbol>.json (long symbols are shortened with a stable hash suffix); the stdout copy is what --quiet and --output=summary replace."
    };

    internal static void PrintUnknownModeError(IOutputSink output)
    {
        var modes = Program.ModeRegistry.Select(entry => $"--mode={entry.Name}").ToList();
        var modeList = string.Join(", ", modes.Take(modes.Count - 1)) + ", or " + modes[^1];
        output.WriteErrorLine($"ERROR: Unknown mode. Use {modeList}.");
        output.WriteErrorLine("  Note: For --mode=index, use --strategy=<incremental|full> (default: full on first run, incremental on subsequent runs).");
        output.WriteErrorLine("    --strategy=full rebuilds a snapshot that an incremental run built and reuses one that a full run built; add --force to rebuild it in every case. Use it to recover when something looks wrong.");
    }

    internal static void PrintHelp(IOutputSink output)
    {
        output.WriteLine("lurp : Roslyn-native semantic context engine for C#");
        output.WriteLine();
        output.WriteLine("USAGE");
        output.WriteLine("  lurp --mode=<mode> [--solution=<path> | --output-dir=<path>] [options]");
        output.WriteLine();
        output.WriteLine("  --output-dir=<path>  Directory where index.db is stored. When omitted,");
        output.WriteLine("                       --solution= resolves the default cache location");
        output.WriteLine($"                       {Path.Combine(LurpCache.CacheRoot(), "<hash of the solution path>")}.");
        output.WriteLine("                       (LURP_OUTPUT_DIR / LURP_SOLUTION_PATH set these too.)");
        output.WriteLine();
        output.WriteLine("MODES");
        foreach (var entry in Program.ModeRegistry)
            output.WriteLine($"  --mode={entry.Name,-20}{entry.HelpText}");
        output.WriteLine();
        output.WriteLine("OPTIONS BY MODE");
        output.WriteLine("  (flags below are exactly the ones each mode accepts, from the mode registry)");
        foreach (var entry in Program.ModeRegistry)
        {
            output.WriteLine();
            output.WriteLine($"  --mode={entry.Name}");
            if (entry.Flags.Length == 0)
                output.WriteLine("      (no flags beyond --output-dir=)");
            foreach (var spec in entry.Specs)
                WriteFlag(spec, output);
            if (ModeNotes.TryGetValue(entry.Name, out var note))
                foreach (var line in WrapWords(PlainText(note), 72))
                    output.WriteLine($"      {line}");
        }

        output.WriteLine();
        output.WriteLine("SNAPSHOT LIFECYCLE");
        output.WriteLine("  Each indexing run (full or incremental) creates a NEW snapshot,");
        output.WriteLine("  unless source content and compilation inputs are unchanged, in which");
        output.WriteLine("  case the existing snapshot is reused (content-addressed dedup).");
        output.WriteLine("  The 3 newest complete snapshots are retained; older ones are pruned automatically.");
        output.WriteLine("  Snapshots are never mutated : a new snapshot is created when content");
        output.WriteLine("  changes, and it does NOT modify the previous one.");
        output.WriteLine();
        output.WriteLine("ENVIRONMENT VARIABLES");
        output.WriteLine("  LURP_SOLUTION_PATH      Equivalent to --solution=.");
        output.WriteLine("  LURP_OUTPUT_DIR         Equivalent to --output-dir=.");
    }

    /// <summary>Removes the Markdown backticks from a flag description for the plain-text help.</summary>
    internal static string PlainText(string markdown) => markdown.Replace("`", "");

    private static void WriteFlag(FlagSpec spec, IOutputSink output)
    {
        var text = spec.Description + ValuesSuffix(spec) + RequirementSuffix(spec);
        var lines = WrapWords(PlainText(text), 52);
        output.WriteLine($"    {spec.Name,-FlagColumn}{lines[0]}");
        var continuationIndent = new string(' ', 4 + FlagColumn);
        for (var i = 1; i < lines.Count; i++)
            output.WriteLine($"{continuationIndent}{lines[i]}");
    }

    private static string ValuesSuffix(FlagSpec spec) =>
        spec.AllowedValues is { Count: > 0 }
            ? " Values: " + string.Join(", ", spec.AllowedValues) + "."
            : "";

    private static string RequirementSuffix(FlagSpec spec) => spec.Requirement switch
    {
        FlagRequirement.Required when spec.EnvironmentVariable is not null => $" Required (or set `{spec.EnvironmentVariable}`).",
        FlagRequirement.Required => " Required.",
        FlagRequirement.Conditional => " Required in some combinations; see the mode's section of docs/CLI_REFERENCE.md.",
        _ => "",
    };

    private static List<string> WrapWords(string text, int width)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                lines.Add(line);
                line = "";
            }

            line = line.Length == 0 ? word : line + " " + word;
        }

        if (line.Length > 0)
            lines.Add(line);
        return lines;
    }
}
