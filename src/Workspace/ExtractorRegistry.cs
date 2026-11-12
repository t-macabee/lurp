using Lurp.Adapters;

namespace Lurp.Workspace;

/// <summary>
///     Single source of truth for every extractor whose version string appears
///     in <c>edges.extractor_version</c>.  The registry is built from
///     <see cref="ExtractorConstants" /> + <see cref="VersionConstants" /> +
///     adapter versions so that the <c>extractors</c> table can be populated
///     idempotently by both full and incremental index runs.
/// </summary>
internal static class ExtractorRegistry
{
    /// <summary>
    ///     Workspace extractors, whose versions live in <see cref="ExtractorConstants" />
    ///     / <see cref="VersionConstants" />. Framework adapters are not listed here —
    ///     see <see cref="All" />.
    /// </summary>
    private static readonly (string Name, string Version, string Description)[] WorkspaceExtractors =
    [
        // -- Member-edge extractors (ExtractorConstants) --
        ("Declares", ExtractorConstants.DeclaresExtractor, "Type-declares-member containment edges"),
        ("Overrides", ExtractorConstants.OverridesExtractor, "Method override edges"),
        ("Hides", ExtractorConstants.HidesExtractor, "Member-hiding (new keyword) edges"),
        ("Returns", ExtractorConstants.ReturnsExtractor, "Return-type reference edges"),
        ("ParameterDependencies", ExtractorConstants.ParameterDependenciesExtractor, "Parameter-type dependency edges"),

        // -- Reflection extractors --
        ("Reflection", ExtractorConstants.ReflectionExtractor, "Reflection-based dependency edges (nameof/typeof/string-literal)"),

        // -- Static-dispatch extractor --
        ("StaticDispatch", ExtractorConstants.StaticallyCallsExtractor, "Static (non-virtual) dispatch call edges"),

        // -- Polymorphism extractor --
        ("Polymorphism", ExtractorConstants.PolymorphismExtractor, "Polymorphic (virtual) dispatch edges"),

        // -- Member-body operation pass (S01-F22): every executable root, explicit and implicit shapes --
        ("OperationShapes", ExtractorConstants.OperationShapesExtractor, "Member-body edges: calls, constructions, reads, writes, method groups, extension receivers, thrown types and body type uses"),

        // -- Structural type-relationship extractor (uses VersionConstants.ExtractorVersion) --
        ("Structural", VersionConstants.ExtractorVersion, "Structural type edges (inherits, implements, contains, references)")
    ];

    /// <summary>(Name, Version, Description) for every known extractor.</summary>
    /// <remarks>
    ///     <c>Version</c> is the exact string written into
    ///     <c>edges.extractor_version</c>.  <c>Name</c> is a short human-readable
    ///     identifier for the extractor. Adapter rows are projected from the adapter
    ///     instances themselves (<see cref="AdapterRegistry.GetAdapters" />), so the
    ///     version an adapter stamps onto its edges and the version registered in the
    ///     <c>extractors</c> table are the same symbol and cannot drift.
    /// </remarks>
    internal static IReadOnlyList<(string Name, string Version, string Description)> All { get; } =
    [
        .. WorkspaceExtractors,
        .. AdapterRegistry.GetAdapters().Select(a => (a.Name, a.Version, a.Description))
    ];
}