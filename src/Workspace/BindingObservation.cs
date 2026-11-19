namespace Lurp.Workspace;

/// <summary>
///     The binding region that decides whether an empty tier is a proved absence (D57).
/// </summary>
internal enum TierBindingScope
{
    /// <summary>The documents that declare the anchor.</summary>
    AnchorDocuments,

    /// <summary>The anchor's project and every project that references it directly or transitively (D177).</summary>
    CallerProjects
}

/// <summary>
///     Whether each binding scope of one capsule request lost bindings. An empty tier in an
///     unobservable scope is <c>unresolved</c>, never <c>empty</c>.
/// </summary>
internal sealed record BindingObservation(
    bool MissingReader,
    bool AnchorDocumentsLost,
    bool CallerScopeUnknown,
    bool CallerProjectsLost)
{
    internal static BindingObservation Create(
        CapsuleAnchor anchor,
        SymbolId symbolId,
        bool missingReader,
        IReadOnlyList<BindingIncompletenessRecord> bindingIncompleteness,
        IReadOnlyList<ProjectRow>? projects,
        CancellationToken cancellationToken)
    {
        var losses = bindingIncompleteness
            .Where(record => BindingIncompletenessReason.UnobservableReasons.Contains(record.Reason))
            .ToList();
        if (missingReader || losses.Count == 0)
            return new BindingObservation(missingReader, false, false, false);

        var callerProjects = CallerProjects(symbolId, projects, cancellationToken);
        return new BindingObservation(
            false,
            AnchorDocumentsHaveLosses(anchor, losses, projects),
            callerProjects == null,
            callerProjects != null && losses.Any(record => callerProjects.Contains(record.ProjectName)));
    }

    internal bool IsUnobservable(TierBindingScope scope) =>
        MissingReader || (scope == TierBindingScope.AnchorDocuments
            ? AnchorDocumentsLost
            : CallerScopeUnknown || CallerProjectsLost);

    /// <summary>
    ///     The <c>omitted_tiers_unresolved</c> text. It names only the causes that hold for
    ///     this request.
    /// </summary>
    internal string Explain(bool missingFriendFacts)
    {
        var sentences = new List<string>
        {
            "An omitted tier marked 'unresolved' means Lurp could not observe the relation. "
            + "It is NOT evidence that no such relation exists. Only tiers marked 'empty' are a proved absence.",
            "Contracts, direct callees and surrounding source use the anchor documents. "
            + "Callers, relevant tests, registered implementations and affected public surfaces use the anchor project "
            + "and the projects that reference it directly or transitively."
        };
        if (MissingReader)
            sentences.Add("The binding reader is unavailable, so Lurp cannot confirm absence in either scope.");
        if (AnchorDocumentsLost)
            sentences.Add("Bindings were lost in an anchor document, or in a project whose documents Lurp could not recover.");
        if (CallerScopeUnknown)
            sentences.Add("Bindings were lost, and Lurp could not recover the projects that can reference the anchor.");
        if (CallerProjectsLost)
            sentences.Add("Bindings were lost in the anchor project or in a project that references it.");
        if (missingFriendFacts)
            sentences.Add("Project facts are unavailable, so Lurp cannot check friend grants for internal public-surface candidates.");
        return string.Join(" ", sentences);
    }

    // A loss without a document path covers its whole project. When project facts or the
    // project's document list are missing, Lurp cannot exclude the anchor documents, so the
    // loss counts. A project without a row is outside the snapshot, as in CallerProjects.
    private static bool AnchorDocumentsHaveLosses(CapsuleAnchor anchor, List<BindingIncompletenessRecord> losses,
        IReadOnlyList<ProjectRow>? projects)
    {
        var anchorDocuments = anchor.Locations
            .Select(location => location.DocumentPath)
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(PathNormalizer.ToForwardSlash)
            .ToHashSet(PathNormalizer.PathComparer);

        return losses.Any(record =>
        {
            if (record.DocumentPath is { } path)
                return anchorDocuments.Contains(PathNormalizer.ToForwardSlash(path));
            if (projects == null)
                return true;

            return projects.Any(project => project.Name == record.ProjectName &&
                (project.DocumentPaths == null || project.DocumentPaths.Any(document =>
                    anchorDocuments.Contains(PathNormalizer.ToForwardSlash(document)))));
        });
    }

    // D177: the projects that compile the anchor's assembly, closed over the stored project
    // references. Null means the scope cannot be recovered.
    private static HashSet<string>? CallerProjects(SymbolId symbolId, IReadOnlyList<ProjectRow>? projects,
        CancellationToken cancellationToken)
    {
        if (projects == null)
            return null;

        var assemblyName = MetadataReferenceIdentity.SimpleName(symbolId.AssemblyIdentity);
        var scope = projects
            .Where(project => ProjectFacts.AssemblyNameComparer.Equals(ProjectFacts.GetAssemblyName(project), assemblyName))
            .Select(project => project.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (scope.Count == 0)
            return null;

        var dependents = projects
            .SelectMany(project => project.References.Select(reference => (Reference: reference, project.Name)))
            .ToLookup(pair => pair.Reference, pair => pair.Name, StringComparer.Ordinal);
        var queue = new Queue<string>(scope);
        while (queue.TryDequeue(out var project))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var dependent in dependents[project])
                if (scope.Add(dependent))
                    queue.Enqueue(dependent);
        }

        return scope;
    }
}
