using System.Text.RegularExpressions;

namespace Lurp.Tests.Docs;

internal static partial class DocTable
{
    [GeneratedRegex(@"^\|\s*`(?<name>[A-Za-z_]+)`\s*\|")]
    private static partial Regex NamedRowPattern();

    internal static IEnumerable<string> RowNames(string markdown, string heading)
    {
        var inSection = false;
        foreach (var (_, text) in DocReferenceChecker.ProseLines(markdown))
        {
            if (text.StartsWith('#'))
            {
                if (inSection)
                    yield break;
                inSection = text.Trim() == heading;
                continue;
            }

            if (inSection && NamedRowPattern().Match(text) is { Success: true } match)
                yield return match.Groups["name"].Value;
        }
    }
}
