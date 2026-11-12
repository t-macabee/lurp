using Lurp.Storage;

namespace Lurp.Tests;

/// <summary>
///     D16 (S01-F19): <c>occurrence_count</c> counts distinct binding sites, that is distinct
///     (document, node span, reason). Expected counts are hand counts of the sites in the source.
/// </summary>
public sealed class BindingSiteCountTests : InMemoryTestBase
{
    private const string Doc = "Sites.cs";

    private static int FilteredExternal(Extraction extraction) =>
        extraction.Result.BindingIncompleteness
            .Where(static r => r.Reason == BindingIncompletenessReason.FilteredExternal && r.DocumentPath?.EndsWith(Doc, StringComparison.Ordinal) == true)
            .Sum(static r => r.Count);

    [Fact]
    public async Task OneExternalCall_IsOneSite()
    {
        // One site: the call System.Console.Beep().
        var extraction = await ExtractAsync(new Dictionary<string, string>
        {
            [Doc] = """
                    namespace N;
                    public class Ext1
                    {
                        public void U() { System.Console.Beep(); }
                    }
                    """
        });

        Assert.Equal(1, FilteredExternal(extraction));
    }

    [Fact]
    public async Task ExternalCallWithAnExternalTypeArgument_IsOneSite()
    {
        // One site: the call node Enumerable.Empty<string>() binds the method Empty and its
        // type argument string, both outside the compilation, at one span.
        var extraction = await ExtractAsync(new Dictionary<string, string>
        {
            [Doc] = """
                    namespace N;
                    public class Ext2
                    {
                        public void U() { System.Linq.Enumerable.Empty<string>(); }
                    }
                    """
        });

        Assert.Equal(1, FilteredExternal(extraction));
    }

    [Fact]
    public async Task DynamicObjectCreation_ConstructsTheTypeAndIsOneLateBoundSite()
    {
        // C#: in 'new Late(d)' with a dynamic argument the created type is fixed at compile time;
        // only the constructor overload binds at run time. So Late gets a Constructs fact, no
        // constructor gets a Calls fact, and the late binding is one unsupported_syntax site.
        var extraction = await ExtractAsync(new Dictionary<string, string>
        {
            [Doc] = """
                    namespace N;
                    public class Late
                    {
                        public Late(int value) { }
                        public Late(string value) { }
                    }
                    public class Maker
                    {
                        public Late Make(dynamic seed) => new Late(seed);
                    }
                    """
        });

        extraction.AssertEdgeSet("Constructs", ("global::N.Maker.Make", "global::N.Late"));
        extraction.AssertEdgeSet("Calls");
        Assert.Equal(1, extraction.Result.BindingIncompleteness
            .Where(static r => r.Reason == BindingIncompletenessReason.UnsupportedSyntax && r.DocumentPath?.EndsWith(Doc, StringComparison.Ordinal) == true)
            .Sum(static r => r.Count));
    }
}
