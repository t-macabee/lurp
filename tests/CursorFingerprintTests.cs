using Lurp.Handlers;
using Lurp.Storage;
using Lurp.Workspace;

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
        Assert.Equal("span\u0001a.cs\u0001True", OutlineCursor.ComputeFingerprint("a.cs", true));
        Assert.Equal("weighted\u0001Foo\u0001\u0001False", SearchCursor.ComputeFingerprint("Foo", null, false));
        Assert.Equal("x\u0001True\u0001False", TextSearchCursor.ComputeFingerprint("x", true, false));
        Assert.Equal("a\u0001\u0001b", SequenceCursor.ComputeFingerprint("a", null, "b"));
    }

    [Fact]
    public void ImpactAndContextFingerprints_CarryTheSequenceResetMarker()
    {
        // 4.2.0 changed both sequences, so the marker makes a 4.1.0 cursor bind to nothing.
        Assert.Equal("impact-v2\u0001M:App.Target.Hit\u0001Upstream\u00013\u0001\u0001\u0001symbols",
            ImpactRequest.ComputeFingerprint("M:App.Target.Hit", ImpactDirection.Upstream, 3, null, null));
        Assert.Equal("capsule-tier-v2\u0001M:App.Target.Hit\u0001direct_callers\u00013\u0001False",
            ContextRequest.ComputeFingerprint("M:App.Target.Hit", "direct_callers", 3, false));
    }
}
