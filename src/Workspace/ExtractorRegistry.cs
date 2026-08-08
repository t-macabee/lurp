using Lurp.Adapters;

namespace Lurp.Workspace;

/// <summary>
/// Single source of truth for every extractor whose version string appears
/// in <c>edges.extractor_version</c>.  The registry is built from
/// <see cref="ExtractorConstants"/> + <see cref="VersionConstants"/> +
/// adapter versions so that the <c>extractors</c> table can be populated
/// idempotently by both full and incremental index runs.
/// </summary>
internal static class ExtractorRegistry
{
    /// <summary>
    /// Workspace extractors, whose versions live in <see cref="ExtractorConstants"/>
    /// / <see cref="VersionConstants"/>. Framework adapters are not listed here —
    /// see <see cref="All"/>.
    /// </summary>
    private static readonly (string Name, string Version, string Description)[] WorkspaceExtractors =
        [
            // -- Member-edge extractors (ExtractorConstants) --
            ("Declares",              ExtractorConstants.DeclaresExtractor,              "Type-declares-member containment edges"),
            ("Calls",                 ExtractorConstants.CallsExtractor,                 "Direct method/function call edges"),
            ("Constructs",            ExtractorConstants.ConstructsExtractor,            "Object-construction (new) edges"),
            ("Overrides",             ExtractorConstants.OverridesExtractor,             "Method override edges"),
            ("Hides",                 ExtractorConstants.HidesExtractor,                 "Member-hiding (new keyword) edges"),
            ("ExtensionReceiver",     ExtractorConstants.ExtensionReceiverExtractor,     "Extension-method receiver binding edges"),
            ("ReadsWrites",           ExtractorConstants.ReadsWritesExtractor,           "Field/property read and write edges"),
            ("Returns",               ExtractorConstants.ReturnsExtractor,               "Return-type reference edges"),
            ("Throws",                ExtractorConstants.ThrowsExtractor,                "Thrown-exception type edges"),
            ("ParameterDependencies", ExtractorConstants.ParameterDependenciesExtractor, "Parameter-type dependency edges"),

            // -- Reflection extractors --
            ("Reflection",            ExtractorConstants.ReflectionExtractor,            "Reflection-based dependency edges (nameof/typeof/string-literal)"),

            // -- Static-dispatch extractor --
            ("StaticDispatch",        ExtractorConstants.StaticallyCallsExtractor,       "Static (non-virtual) dispatch call edges"),

            // -- Polymorphism extractor --
            ("Polymorphism",          ExtractorConstants.PolymorphismExtractor,          "Polymorphic (virtual) dispatch edges"),

            // -- Structural type-relationship extractor (uses VersionConstants.ExtractorVersion) --
            ("Structural",            VersionConstants.ExtractorVersion,                 "Structural type edges (inherits, implements, contains, references)"),
        ];

    /// <summary>(Name, Version, Description) for every known extractor.</summary>
    /// <remarks>
    /// <c>Version</c> is the exact string written into
    /// <c>edges.extractor_version</c>.  <c>Name</c> is a short human-readable
    /// identifier for the extractor. Adapter rows are projected from the adapter
    /// instances themselves (<see cref="AdapterRegistry.GetAdapters"/>), so the
    /// version an adapter stamps onto its edges and the version registered in the
    /// <c>extractors</c> table are the same symbol and cannot drift.
    /// </remarks>
    internal static IReadOnlyList<(string Name, string Version, string Description)> All { get; } =
        [
            .. WorkspaceExtractors,
            .. AdapterRegistry.GetAdapters().Select(a => (a.Name, a.Version, a.Description)),
        ];
}
