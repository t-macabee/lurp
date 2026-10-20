namespace Lurp.Storage;

/// <summary>
/// Single owner of every uncertainty description text that both the dead-candidates
/// surface and the context-capsule surface emit. Each surface keeps its own
/// uncertainty kind and symbol list; only the prose is shared here.
/// </summary>
public static class UncertaintyDescriptions
{
    public static string DispatchCandidate(string targetSymbolId, string provenance) =>
        $"Dispatch candidate '{targetSymbolId}' was resolved with evidence level '{provenance}'. Manually verify that the runtime dispatch reaches the correct implementation.";

    public static string FrameworkConvention(string edgeKind) =>
        $"Convention-based framework binding: the '{edgeKind}' edge was inferred by naming convention, not explicit registration. Verify that the expected target is reached at runtime.";

    public static string NameCandidate(string targetSymbolId) =>
        $"Reflection name candidate: the string-based reference to '{targetSymbolId}' was matched by name. Verify that this reference correctly resolves at runtime.";

    public const string UnknownReflectionTarget =
        "Unknown reflection target: the runtime target of this reflection call cannot be statically determined.";

    public static string GeneratedExcludedFromCapsule(string symbolId) =>
        $"Generated symbol '{symbolId}' was excluded because includeGenerated is set to false. Review generated code if runtime behavior depends on it.";

    public static string GeneratedDeadCandidate(string symbolId) =>
        $"Generated symbol '{symbolId}': at least one declaration is generated code (is_generated=1) and no LIVE incoming edge reaches it. Code that the generator or framework owns often calls generated members; if the member is truly unused, change the generator's input, not the generated file.";
}
