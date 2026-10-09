namespace Lurp.Storage;

public static class BindingIncompletenessReason
{
    public const string AmbiguousOverload = "ambiguous_overload";
    public const string CompilerError = "compiler_error";
    public const string UnresolvedMetadata = "unresolved_metadata";
    public const string UnsupportedSyntax = "unsupported_syntax";
    public const string FilteredExternal = "filtered_external";
    public const string ExtractorFailure = "extractor_failure";

    /// <summary>
    ///     A DI convention scan site whose match set is open: any type added to the
    ///     scanned assembly may newly match, and no persisted edge witnesses the new
    ///     match, so the relation set for the site is never provably complete.
    /// </summary>
    public const string ConventionScan = "convention_scan";

    /// <summary>The whole project failed to load or extract; no binding over it was observable.</summary>
    public const string ProjectUnreadable = "project_unreadable";

    /// <summary>
    ///     Reasons under which a missing relation proves nothing, because the relation was
    ///     never observable. Excludes <see cref="FilteredExternal" />: there the target was
    ///     resolved and is knowably outside the snapshot, which is an explained absence
    ///     rather than an unknown one.
    /// </summary>
    public static readonly IReadOnlySet<string> UnobservableReasons =
        new HashSet<string>(StringComparer.Ordinal)
        {
            AmbiguousOverload,
            CompilerError,
            UnresolvedMetadata,
            UnsupportedSyntax,
            ExtractorFailure,
            ProjectUnreadable,
            ConventionScan
        };

    public static string Describe(string reason, int count, string scope)
    {
        return reason switch
        {
            CompilerError =>
                $"{count} binding(s) in {scope} could not be completed because the snapshot compilation reported compiler errors in those projects. Relations that depend on that code may be missing from the graph even though the references exist in source.",
            UnresolvedMetadata =>
                $"{count} binding(s) in {scope} could not be resolved against project metadata (for example missing package or project references). Relations that depend on those bindings may not be persisted even though the references exist in source.",
            FilteredExternal =>
                $"{count} binding(s) in {scope} resolved to symbols in assemblies outside the compilation. Edges to those external targets are intentionally filtered from the persisted graph; their absence is a declared boundary, not an extraction failure.",
            AmbiguousOverload =>
                $"{count} binding(s) in {scope} were ambiguous, so no unique overload target could be selected. Dispatch targets for those call sites are uncertain.",
            UnsupportedSyntax =>
                $"{count} binding(s) in {scope} could not be completed because the extractor does not support the relevant syntax. Relations at those sites may be missing.",
            ExtractorFailure =>
                $"{count} extractor failure(s) were recorded while producing the snapshot for {scope}. Some relations may be missing.",
            ProjectUnreadable =>
                $"{count} binding(s) in {scope} could not be completed because the project was unreadable.",
            ConventionScan =>
                $"{count} binding(s) in {scope} are convention-scan sites with an open match set.",
            _ =>
                $"{count} binding-incompleteness record(s) (reason '{reason}') affect {scope}. Relations in that code may be incomplete."
        };
    }
}
