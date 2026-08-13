using System.Diagnostics;
using System.Text.Json;
using Lurp.Shared;
using Lurp.Workspace;
using Xunit;

namespace Lurp.Tests;

public class SuggestedVerificationCommandTests
{
    [Fact]
    public void TestName_Normalization_StripsGlobalPrefix_FromCommandOnly()
    {
        var testId = "global::A.B.C|assembly";
        var testName = "global::A.B.C";
        var command = FqnNormalizer.NormalizeForCommand(testName);

        Assert.Equal("A.B.C", command);
        Assert.DoesNotContain("global::", command);
    }

    [Fact]
    public void TestName_Normalization_PreservesGlobalPrefix_InDisplayFields()
    {
        var testId = "global::A.B.C|assembly";
        var testName = "global::A.B.C";
        var normalizedTestName = FqnNormalizer.NormalizeForCommand(testName);

        Assert.Equal("global::A.B.C", testName);
        Assert.Equal("A.B.C", normalizedTestName);
    }

    [Fact]
    public void TestName_Normalization_HandlesNullOrEmpty()
    {
        var nullName = FqnNormalizer.NormalizeForCommand(null);
        var emptyName = FqnNormalizer.NormalizeForCommand(string.Empty);

        Assert.Null(nullName);
        Assert.Equal(string.Empty, emptyName);
    }

    [Fact]
    public void TestName_Normalization_HandlesAlreadyNormalized()
    {
        var normalizedName = FqnNormalizer.NormalizeForCommand("A.B.C");

        Assert.Equal("A.B.C", normalizedName);
    }

    [Fact]
    public void TestName_Normalization_HandlesTheoryTests()
    {
        var theoryName = "global::N.TestClass.TheoryMethod(x: 1)";
        var normalizedName = FqnNormalizer.NormalizeForCommand(theoryName);

        Assert.Equal("N.TestClass.TheoryMethod(x: 1)", normalizedName);
    }
}
