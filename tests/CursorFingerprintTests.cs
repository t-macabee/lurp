using Lurp.Storage;

namespace Lurp.Tests;

public sealed class CursorFingerprintTests
{
    [Fact]
    public void ComputeFingerprint_JoinsPartsWithU0001_ForEveryCursor()
    {
        Assert.Equal("S\u0001\u0001note", AnnotationCursor.ComputeFingerprint("S", null, "note"));
        Assert.Equal("P\u0001\u0001Method\u0001True\u0001False\u0001True",
            DeadCandidateCursor.ComputeFingerprint("P", null, "Method", true, false, true));
        Assert.Equal("\u0001a.cs\u0001Warning\u0001True\u0001False\u0001CS0219",
            DiagnosticsCursor.ComputeFingerprint(null, "a.cs", "Warning", true, false, "CS0219"));
        Assert.Equal("a.cs\u0001True", OutlineCursor.ComputeFingerprint("a.cs", true));
        Assert.Equal("Foo\u0001\u0001False", SearchCursor.ComputeFingerprint("Foo", null, false));
        Assert.Equal("x\u0001True\u0001False", TextSearchCursor.ComputeFingerprint("x", true, false));
        Assert.Equal("a\u0001\u0001b", SequenceCursor.ComputeFingerprint("a", null, "b"));
    }
}
