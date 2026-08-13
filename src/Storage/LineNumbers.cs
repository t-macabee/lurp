// Purpose: the single authority for the line-number base boundary (audit T4).
// Storage keeps Roslyn-native 0-based line numbers (LinePosition.Line, and the
// indexes FindLineIndex returns into line_starts). Every line value that reaches
// a consumer - impact/context/diff payloads, capsule locations, summaries - is
// 1-based, matching the documented --line= input convention. All 0-to-1
// conversions MUST go through ToOneBased so the boundary is auditable in exactly
// one place; adding a bare `+ 1` at a new emit site is a convention violation.
//
// This type lives in Lurp.Storage (not Lurp.Shared) on purpose: Lurp.Storage is
// a separate project that Lurp references, so a helper here is visible to both
// the Storage assembly and every Lurp consumer. Lurp.Shared compiles into the
// Lurp project only and would be unreachable from Storage.
//
// The input direction (--line=) is already 1-based and is converted to 0-based
// by NavigateToLocation/ResolveSymbolByLocation (line - 1); that side is left
// as-is on purpose.

namespace Lurp.Storage;

public static class LineNumbers
{
    public static int ToOneBased(int zeroBasedLine) => zeroBasedLine + 1;

    public static int? ToOneBased(int? zeroBasedLine) => zeroBasedLine + 1;
}
