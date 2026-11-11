using System.Text.RegularExpressions;

namespace Lurp.Tests;

/// <summary>
///     An assertion that cannot fail turns a test green without checking anything. These
///     patterns are the ones that have shown up in this suite; the scan keeps them out. The scan
///     reads each file as one text, so an assertion that spans lines is seen as one statement.
/// </summary>
public sealed class VacuousAssertionGuardTests
{
    private const string Size = @"(Count|Length|GetArrayLength\(\)|Count\(\))";

    // [^;]* and \s* cross line breaks, so each pattern matches inside one statement, on one line or on many.
    private static readonly Regex[] Vacuous =
    [
        // A size can never be negative, so these pass for an empty result as well as a full one.
        new(@"Assert\.True\([^;]*\b" + Size + @"\s*(>=\s*0|>\s*-\s*1)\b", RegexOptions.Compiled),
        new(@"Assert\.False\([^;]*\b" + Size + @"\s*<\s*0\b", RegexOptions.Compiled),
        new(@"Assert\.True\(\s*true\b|Assert\.False\(\s*false\b", RegexOptions.Compiled),
        // A null string is already empty, so the second operand adds nothing.
        new(@"string\.IsNullOrEmpty\((\w+)\)\s*\|\|\s*\1\s*==\s*null", RegexOptions.Compiled)
    ];

    [Fact]
    public void TestSources_ContainNoAssertionThatCannotFail()
    {
        var testsDir = Path.Combine(TestRepoRoot.Find(), "tests");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file, testsDir) || Path.GetFileName(file) == nameof(VacuousAssertionGuardTests) + ".cs")
                continue;

            foreach (var (line, statement) in FindVacuous(File.ReadAllText(file)))
                offenders.Add($"{Path.GetRelativePath(testsDir, file)}:{line}: {statement}");
        }

        Assert.True(offenders.Count == 0,
            $"assertions that cannot fail:\n{string.Join("\n", offenders)}");
    }

    [Theory]
    [InlineData("Assert.True(results.Count >= 0);")]
    [InlineData("Assert.True(doc.RootElement.GetProperty(\"r\").GetArrayLength() >= 0);")]
    [InlineData("Assert.True(true);")]
    [InlineData("Assert.True(string.IsNullOrEmpty(cursor) || cursor == null);")]
    [InlineData("Assert.True(\n    results.Count >= 0);")]
    [InlineData("Assert.True(\r\n    results.Count >= 0,\r\n    \"never fails\");")]
    [InlineData("Assert.True(results.Count > -1);")]
    [InlineData("Assert.False(results.Count < 0);")]
    public void Detector_FlagsKnownVacuousAssertions(string source)
    {
        Assert.Single(FindVacuous(source));
    }

    [Theory]
    [InlineData("Assert.True(start >= 0, \"anchor is missing\");")]
    [InlineData("Assert.True(results.Count > 0);")]
    [InlineData("Assert.True(string.IsNullOrEmpty(cursor));")]
    [InlineData("Assert.True(\n    results.Count > 0);\nvar n = list.Count >= 0;")]
    public void Detector_LeavesMeaningfulAssertionsAlone(string source)
    {
        Assert.Empty(FindVacuous(source));
    }

    // Each match, with its 1-based start line and its text on one line.
    private static IEnumerable<(int Line, string Statement)> FindVacuous(string source) =>
        Vacuous
            .SelectMany(pattern => pattern.Matches(source))
            .OrderBy(match => match.Index)
            .Select(match => (1 + source.AsSpan(0, match.Index).Count('\n'), Regex.Replace(match.Value, @"\s+", " ")));

    private static bool IsBuildOutput(string file, string testsDir)
    {
        var relative = Path.GetRelativePath(testsDir, file).Replace('\\', '/');
        return relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal);
    }
}
