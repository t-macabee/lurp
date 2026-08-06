using Lurp.Handlers;

namespace Lurp;

/// <summary>
/// Rejects flags a mode does not read.
/// <para>
/// Before this existed, an unrecognised flag was silently dropped: <c>--max-hop=1</c>
/// (singular) left <c>maxHops</c> at its default of 3 and the capsule then reported
/// <c>maxHops: 3</c> as though that had been asked for, and <c>--solution=</c> on
/// <c>--mode=simulate-rename</c> was accepted although that mode never reads it. Both
/// produced a confident answer to a different question than the one posed, which is the
/// costly failure for a tool whose value is that its facts can be trusted. Unknown
/// <em>modes</em> already hard-failed; unknown flags were simply the half nobody wrote.
/// </para>
/// </summary>
internal static class CliFlagValidation
{
    /// <summary>
    /// Accepted by every mode. <c>--mode=</c> selects the mode, <c>--output-dir=</c> is read
    /// by every handler through <see cref="HandlerBootstrap.ResolveOutputDir"/>, and the help
    /// flags are handled before dispatch but may still appear in the argument list.
    /// </summary>
    private static readonly string[] GlobalFlags =
    [
        "--mode=",
        "--output-dir=",
        "--help",
        "-h",
    ];

    /// <summary>
    /// Validates <paramref name="args"/> against the inventory <paramref name="entry"/>
    /// declares, and fails with the mode's valid flags listed. Only dash-prefixed tokens are
    /// checked: no mode takes positional arguments today, and leaving bare tokens alone keeps
    /// this from rejecting something a caller passes for reasons the registry cannot see.
    /// </summary>
    public static void Validate(Program.ModeRegistryEntry entry, string[] args)
    {
        var allowed = new HashSet<string>(GlobalFlags, StringComparer.Ordinal);
        foreach (var flag in entry.Flags)
            allowed.Add(flag);

        foreach (var arg in args)
        {
            if (!arg.StartsWith('-'))
                continue;

            // A valued flag is matched including its '=', so the shape is checked too:
            // "--quiet=5" does not match the bare "--quiet", and a bare "--budget" does
            // not match "--budget=". Either mistake silently produced a default before.
            var separator = arg.IndexOf('=');
            var token = separator >= 0 ? arg[..(separator + 1)] : arg;

            if (allowed.Contains(token))
                continue;

            HandlerBootstrap.Fail(BuildError(entry, token, allowed));
        }
    }

    private static string BuildError(Program.ModeRegistryEntry entry, string token, HashSet<string> allowed)
    {
        var lines = new List<string>
        {
            $"ERROR: unknown flag '{token}' for --mode={entry.Name}.",
        };

        var suggestion = NearestMatch(token, allowed);
        if (suggestion != null)
            lines.Add($"  Did you mean '{suggestion}'?");

        lines.Add($"  Valid flags: {string.Join(", ", allowed.OrderBy(static f => f, StringComparer.Ordinal))}");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The closest allowed flag within one edit, or null. This only enriches the message :
    /// the rejection above does not depend on it, because the flag that caused this work
    /// (<c>--solution</c> on a mode that ignores it) is a real flag borrowed from another
    /// mode and is nowhere near an edit-distance match.
    /// </summary>
    private static string? NearestMatch(string token, HashSet<string> allowed)
    {
        var bare = token.TrimEnd('=');
        string? best = null;

        foreach (var candidate in allowed)
        {
            if (EditDistanceWithin1(bare, candidate.TrimEnd('=')))
            {
                best = candidate;
                break;
            }
        }

        return best;
    }

    /// <summary>True when <paramref name="a"/> reaches <paramref name="b"/> in at most one
    /// insertion, deletion, or substitution.</summary>
    private static bool EditDistanceWithin1(string a, string b)
    {
        var lengthGap = a.Length - b.Length;
        if (lengthGap is < -1 or > 1)
            return false;

        int i = 0, j = 0;
        var edited = false;

        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j])
            {
                i++;
                j++;
                continue;
            }

            if (edited)
                return false;

            edited = true;

            // Consume from the longer side on a length mismatch, both sides when equal.
            if (a.Length > b.Length) i++;
            else if (a.Length < b.Length) j++;
            else { i++; j++; }
        }

        return true;
    }
}
