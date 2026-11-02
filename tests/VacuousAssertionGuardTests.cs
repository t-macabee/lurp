using System.Text.RegularExpressions;
using Lurp.Tests.Docs;

namespace Lurp.Tests;

/// <summary>
///     An assertion that cannot fail turns a test green without checking anything. These
///     patterns are the ones that have shown up in this suite; the scan keeps them out.
/// </summary>
public sealed class VacuousAssertionGuardTests
{
    private static readonly Regex[] Vacuous =
    [
        // A length can never be negative, so this passes for an empty result as well as a full one.
        new(@"Assert\.True\([^;]*\b(Count|Length|GetArrayLength\(\)|Count\(\))\s*>=\s*0\b", RegexOptions.Compiled),
        new(@"Assert\.True\(\s*true\b|Assert\.False\(\s*false\b", RegexOptions.Compiled),
        // A null string is already empty, so the second operand adds nothing.
        new(@"string\.IsNullOrEmpty\((\w+)\)\s*\|\|\s*\1\s*==\s*null", RegexOptions.Compiled)
    ];

    [Fact]
    public void TestSources_ContainNoAssertionThatCannotFail()
    {
        var testsDir = Path.Combine(DocCatalog.RepoRoot(), "tests");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file, testsDir) || Path.GetFileName(file) == nameof(VacuousAssertionGuardTests) + ".cs")
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (Vacuous.Any(pattern => pattern.IsMatch(lines[i])))
                    offenders.Add($"{Path.GetRelativePath(testsDir, file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            $"assertions that cannot fail:\n{string.Join("\n", offenders)}");
    }

    [Theory]
    [InlineData("Assert.True(results.Count >= 0);")]
    [InlineData("Assert.True(doc.RootElement.GetProperty(\"r\").GetArrayLength() >= 0);")]
    [InlineData("Assert.True(true);")]
    [InlineData("Assert.True(string.IsNullOrEmpty(cursor) || cursor == null);")]
    public void Detector_FlagsKnownVacuousAssertions(string line)
    {
        Assert.Contains(Vacuous, pattern => pattern.IsMatch(line));
    }

    [Theory]
    [InlineData("Assert.True(start >= 0, \"anchor is missing\");")]
    [InlineData("Assert.True(results.Count > 0);")]
    [InlineData("Assert.True(string.IsNullOrEmpty(cursor));")]
    public void Detector_LeavesMeaningfulAssertionsAlone(string line)
    {
        Assert.DoesNotContain(Vacuous, pattern => pattern.IsMatch(line));
    }

    private static bool IsBuildOutput(string file, string testsDir)
    {
        var relative = Path.GetRelativePath(testsDir, file).Replace('\\', '/');
        return relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal);
    }
}
