using Lurp.Workspace;
using Microsoft.CodeAnalysis.CSharp;

namespace Lurp.Tests;

public sealed class DeclarationChangeTests
{
    public static TheoryData<string, string, bool> EditCases => new()
    {
        // Body-only edits (expected true)
        {
            "namespace N; public class C { void M() { int x = 1; } }",
            "namespace N; public class C { void M() { int x = 2; } }",
            true
        },
        {
            "namespace N; public class C { int M() => 1; }",
            "namespace N; public class C { int M() => 2; }",
            true
        },
        {
            "namespace N; public class C { int P { get { return 1; } } }",
            "namespace N; public class C { int P { get { return 2; } } }",
            true
        },
        {
            "namespace N; public class C { void M() { } }",
            "namespace N;\n// a comment\npublic class C { void M() { } }",
            true
        },
        {
            "namespace N; public class C { void M() { } }",
            "namespace N;\n/// <summary>Doc</summary>\npublic class C { void M() { } }",
            true
        },
        {
            "namespace N; public class C { int f = 1; }",
            "namespace N; public class C { int f = 2; }",
            true
        },

        // Declaration-level edits (expected false)
        {
            "namespace N; public class C { void A() { } }",
            "namespace N; public class C { void A() { } void B() { } }",
            false
        },
        {
            "namespace N; public class C { void M(int x) { } }",
            "namespace N; public class C { void M(long x) { } }",
            false
        },
        {
            "namespace N; public class C { int M() => 1; }",
            "namespace N; public class C { long M() => 1; }",
            false
        },
        {
            "namespace N; public class C { private void M() { } }",
            "namespace N; public class C { public void M() { } }",
            false
        },
        {
            "namespace N; public class C { }",
            "using System.Linq;\nnamespace N; public class C { }",
            false
        },
        {
            "global using Shape = App.Square;",
            "global using Shape = App.Circle;",
            false
        },
        {
            "namespace N; public class C { }",
            "extern alias X;\nnamespace N; public class C { }",
            false
        },
        {
            "using System;\nnamespace N; public class C { }",
            "using System;\nnamespace N; [Obsolete] public class C { }",
            false
        },
        {
            "using System;\nnamespace N; [Obsolete(\"a\")] public class C { }",
            "using System;\nnamespace N; [Obsolete(\"b\")] public class C { }",
            false
        },
        {
            "namespace N; public class C { const int K = 1; }",
            "namespace N; public class C { const int K = 2; }",
            false
        },
        {
            "namespace N; public static class C { public static int F(string s) => 1; }",
            "namespace N; public static class C { public static int F(this string s) => 1; }",
            false
        }
    };

    [Theory]
    [MemberData(nameof(EditCases))]
    public void IsBodyOnlyEdit_ClassifiesSyntaxChanges(string previousText, string newText, bool expectedBodyOnly)
    {
        var newTree = CSharpSyntaxTree.ParseText(newText, path: "Test.cs");
        Assert.Equal(expectedBodyOnly, DeclarationChange.IsBodyOnlyEdit(newTree, previousText));
    }

    [Fact]
    public void IsBodyOnlyEdit_NullPreviousText_ReturnsFalse()
    {
        var newTree = CSharpSyntaxTree.ParseText("namespace N; public class C { }", path: "Test.cs");
        Assert.False(DeclarationChange.IsBodyOnlyEdit(newTree, null));
    }
}
