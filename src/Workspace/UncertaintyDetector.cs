namespace Lurp.Workspace;

internal sealed class UncertaintyDetector
{
    private readonly IReadOnlyList<BindingIncompletenessRecord> _bindingIncompleteness;
    private readonly CancellationToken _cancellationToken;
    private readonly IDeclarationStore _declarationStore;
    private readonly IEdgeStore _edgeStore;
    private readonly bool _includeGenerated;
    private readonly string _snapshotId;
    private readonly SnapshotRow? _snapshotMetadata;
    private readonly SymbolId _symbolId;

    public UncertaintyDetector(
        IEdgeStore edgeStore,
        IDeclarationStore declarationStore,
        string snapshotId,
        SymbolId symbolId,
        bool includeGenerated,
        SnapshotRow? snapshotMetadata = null,
        IReadOnlyList<BindingIncompletenessRecord>? bindingIncompleteness = null,
        CancellationToken cancellationToken = default)
    {
        _edgeStore = edgeStore;
        _declarationStore = declarationStore;
        _snapshotId = snapshotId;
        _symbolId = symbolId;
        _includeGenerated = includeGenerated;
        _snapshotMetadata = snapshotMetadata;
        _bindingIncompleteness = bindingIncompleteness ?? [];
        _cancellationToken = cancellationToken;
    }

    internal void PopulateUncertainties(ContextCapsule capsule, IEnumerable<AnnotationRecord> mediatRPatterns)
    {
        var neighborhood = BuildNeighborhood(capsule);

        // One entry for each uncertain relation (S01-F18): the loops below visit the
        // incoming and the outgoing edges of every neighborhood node, so an edge whose
        // two ends are both in the neighborhood is seen twice.
        var relations = new HashSet<(string Kind, string Source, string Target)>();
        CollectReflectionUncertainties(capsule, neighborhood, relations);
        CollectDispatchUncertainties(capsule, neighborhood, relations);
        CollectMissingReceiverConstraintUncertainties(capsule, neighborhood);
        CollectFrameworkConventionUncertainties(capsule, neighborhood, relations);
        CollectRuntimeUnknownUncertainties(capsule, neighborhood, relations);
        CollectBindingIncompletenessUncertainties(capsule);
        CollectUnmodeledMediatRPatternUncertainties(capsule, mediatRPatterns);

        if (!_includeGenerated)
            CollectGeneratedExclusionUncertainties(capsule, neighborhood);
    }

    private HashSet<string> BuildNeighborhood(ContextCapsule capsule)
    {
        var neighborhood = new HashSet<string> { _symbolId.Value };

        AddSymbolIds(neighborhood, capsule.Contracts);
        AddSymbolIds(neighborhood, capsule.DirectCallees);
        AddSymbolIds(neighborhood, capsule.DirectCallers);
        AddSymbolIds(neighborhood, capsule.RegisteredImplementations);
        AddSymbolIds(neighborhood, capsule.RelevantTests);
        AddSymbolIds(neighborhood, capsule.SecondDegreeContext);
        AddSymbolIds(neighborhood, capsule.SurroundingSource);

        var anchorEdges = _edgeStore.GetIncomingEdges(_snapshotId, _symbolId.Value)
            .Concat(_edgeStore.GetOutgoingEdges(_snapshotId, _symbolId.Value))
            .ToList();

        foreach (var edge in anchorEdges)
        {
            neighborhood.Add(edge.SourceSymbolId);
            neighborhood.Add(edge.TargetSymbolId);
        }

        return neighborhood;
    }

    private static void AddSymbolIds(HashSet<string> neighborhood, IEnumerable<CapsuleItem> items)
    {
        foreach (var item in items)
            neighborhood.Add(item.SymbolId);
    }

    private static void AddRelationUncertainty(
        ContextCapsule capsule, HashSet<(string Kind, string Source, string Target)> relations,
        EdgeRecord edge, string description)
    {
        if (relations.Add((edge.Kind, edge.SourceSymbolId, edge.TargetSymbolId)))
            capsule.Uncertainties.Add(new UncertaintyEntry([edge.SourceSymbolId, edge.TargetSymbolId], edge.Kind, description));
    }

    private void CollectReflectionUncertainties(ContextCapsule capsule, HashSet<string> neighborhood, HashSet<(string Kind, string Source, string Target)> relations)
    {
        foreach (var symbolId in neighborhood)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var edges = _edgeStore.GetIncomingEdges(_snapshotId, symbolId)
                .Concat(_edgeStore.GetOutgoingEdges(_snapshotId, symbolId));

            foreach (var edge in edges.Where(edge => _includeGenerated || !edge.IsCrossGenerated))
                if (edge.Kind == nameof(EdgeKind.ReflectionNameCandidate))
                    AddRelationUncertainty(capsule, relations, edge, UncertaintyDescriptions.NameCandidate(edge.TargetSymbolId));
                else if (edge.Kind == nameof(EdgeKind.ReflectionTargetUnknown))
                    AddRelationUncertainty(capsule, relations, edge, UncertaintyDescriptions.UnknownReflectionTarget);
        }
    }

    private void CollectDispatchUncertainties(ContextCapsule capsule, HashSet<string> neighborhood, HashSet<(string Kind, string Source, string Target)> relations)
    {
        foreach (var symbolId in neighborhood)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var outgoing = _edgeStore.GetOutgoingEdges(_snapshotId, symbolId);
            foreach (var edge in outgoing)
            {
                if ((!_includeGenerated && edge.IsCrossGenerated) || edge.Kind != nameof(EdgeKind.MayDispatchTo))
                    continue;
                if (edge.Provenance is Provenance.CompilerProved or Provenance.FrameworkDerived)
                    continue;

                AddRelationUncertainty(capsule, relations, edge, UncertaintyDescriptions.DispatchCandidate(edge.TargetSymbolId, edge.Provenance));
            }
        }
    }

    private void CollectMissingReceiverConstraintUncertainties(ContextCapsule capsule, HashSet<string> neighborhood)
    {
        var seen = new HashSet<(string Source, string Target)>();
        foreach (var symbolId in neighborhood)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            foreach (var call in _edgeStore.GetOutgoingEdges(_snapshotId, symbolId))
            {
                if ((!_includeGenerated && call.IsCrossGenerated) || call.Kind != nameof(EdgeKind.Calls) ||
                    ReceiverTypeConstraints.Deserialize(call.ReceiverTypeConstraintsJson).Count > 0 ||
                    !seen.Add((call.SourceSymbolId, call.TargetSymbolId)))
                    continue;

                _cancellationToken.ThrowIfCancellationRequested();
                var hasDispatchRelation = _edgeStore.GetOutgoingEdges(_snapshotId, call.TargetSymbolId)
                    .Any(edge => (_includeGenerated || !edge.IsCrossGenerated) && edge.Kind == nameof(EdgeKind.MayDispatchTo));
                if (!hasDispatchRelation)
                    continue;

                capsule.Uncertainties.Add(new UncertaintyEntry(
                    [call.SourceSymbolId, call.TargetSymbolId],
                    "receiver_constraints_unavailable",
                    "No call-site dispatch candidates were emitted because this Calls relation has no persisted " +
                    "static receiver-type constraints. This is expected for legacy snapshots, dynamic/base/static " +
                    "dispatch, or generic constraints the persisted type graph cannot prove; query the called " +
                    "member's global implementations separately if needed."));
            }
        }
    }

    private void CollectFrameworkConventionUncertainties(ContextCapsule capsule, HashSet<string> neighborhood, HashSet<(string Kind, string Source, string Target)> relations)
    {
        var frameworkKinds = new HashSet<string>
        {
            nameof(EdgeKind.RoutesTo),
            nameof(EdgeKind.Handles),
            nameof(EdgeKind.Registers)
        };

        foreach (var symbolId in neighborhood)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var edges = _edgeStore.GetIncomingEdges(_snapshotId, symbolId)
                .Concat(_edgeStore.GetOutgoingEdges(_snapshotId, symbolId));

            foreach (var edge in edges)
            {
                if ((!_includeGenerated && edge.IsCrossGenerated) || !frameworkKinds.Contains(edge.Kind))
                    continue;
                if (edge.Provenance != Provenance.Convention)
                    continue;

                AddRelationUncertainty(capsule, relations, edge, UncertaintyDescriptions.FrameworkConvention(edge.Kind));
            }
        }
    }

    private void CollectRuntimeUnknownUncertainties(ContextCapsule capsule, HashSet<string> neighborhood, HashSet<(string Kind, string Source, string Target)> relations)
    {
        foreach (var symbolId in neighborhood)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var edges = _edgeStore.GetIncomingEdges(_snapshotId, symbolId)
                .Concat(_edgeStore.GetOutgoingEdges(_snapshotId, symbolId));

            foreach (var edge in edges)
            {
                if ((!_includeGenerated && edge.IsCrossGenerated) || edge.Kind != nameof(EdgeKind.Registers))
                    continue;
                if (edge.Provenance != Provenance.RuntimeUnknown)
                    continue;

                AddRelationUncertainty(capsule, relations, edge, DeclaredBoundaries.UncertaintyDescription(edge.Kind));
            }
        }
    }

    // Translates persisted binding-incompleteness rows that fall inside the
    // capsule's document scope (anchor documents, tier items, and traversed
    // path hops) into bounded uncertainty entries. Rows are aggregated by
    // reason so the section stays small, and the wording distinguishes
    // compiler_error, unresolved_metadata, and filtered_external without
    // implying that a missing relation means the reference is absent from
    // source.
    private void CollectBindingIncompletenessUncertainties(ContextCapsule capsule)
    {
        if (_bindingIncompleteness.Count == 0)
            return;

        var documentScope = BuildIncompletenessDocumentScope(capsule);
        if (documentScope.Count == 0)
            return;

        var byDocument = _bindingIncompleteness
            .Where(record => record.DocumentPath != null && documentScope.Contains(record.DocumentPath))
            .ToList();
        if (byDocument.Count == 0)
            return;

        // Project-level rows (no document path, e.g. extractor_failure) are
        // relevant when the owning project contributes an in-scope document.
        var relevantProjects = new HashSet<string>(
            byDocument.Select(static record => record.ProjectName), StringComparer.Ordinal);
        var projectLevel = _bindingIncompleteness
            .Where(record => record.DocumentPath == null && relevantProjects.Contains(record.ProjectName))
            .ToList();

        foreach (var group in byDocument.Concat(projectLevel)
                     .GroupBy(static record => record.Reason, StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            var count = group.Sum(static record => record.Count);
            var projects = group.Select(static record => record.ProjectName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToList();
            var scope = string.Join(", ", projects);
            capsule.Uncertainties.Add(new UncertaintyEntry(
                [_symbolId.Value],
                "binding_incompleteness",
                BindingIncompletenessReason.Describe(group.Key, count, scope)));
        }
    }

    private HashSet<string> BuildIncompletenessDocumentScope(ContextCapsule capsule)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var location in _declarationStore.GetDeclarationLocations(_symbolId.Value, _snapshotId, _includeGenerated))
            paths.Add(location.DocumentPath);
        AddItemDocumentPaths(paths, capsule.Contracts);
        AddItemDocumentPaths(paths, capsule.DirectCallees);
        AddItemDocumentPaths(paths, capsule.DirectCallers);
        AddItemDocumentPaths(paths, capsule.RegisteredImplementations);
        AddItemDocumentPaths(paths, capsule.RelevantTests);
        AddItemDocumentPaths(paths, capsule.SecondDegreeContext);
        AddItemDocumentPaths(paths, capsule.SurroundingSource);
        foreach (var hop in capsule.IncomingPaths.Concat(capsule.OutgoingPaths).SelectMany(static path => path.Hops))
            if (hop.SourceDocument != null)
                paths.Add(hop.SourceDocument);
        return paths;
    }

    private static void AddItemDocumentPaths(HashSet<string> paths, IEnumerable<CapsuleItem> items)
    {
        foreach (var item in items)
            if (item.DocumentPath != null)
                paths.Add(item.DocumentPath);
    }

    private void CollectGeneratedExclusionUncertainties(ContextCapsule capsule, HashSet<string> neighborhood)
    {
        foreach (var symbolId in neighborhood)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var hasGeneratedSource = _declarationStore.GetSymbolSource(symbolId, _snapshotId, ViewKind.Declaration, true);
            var hasNonGeneratedSource = _declarationStore.GetSymbolSource(symbolId, _snapshotId, ViewKind.Declaration);

            if (hasGeneratedSource != null && hasNonGeneratedSource == null)
                capsule.Uncertainties.Add(new UncertaintyEntry([symbolId], "generated_excluded", UncertaintyDescriptions.GeneratedExcludedFromCapsule(symbolId)));
        }
    }

    private static void CollectUnmodeledMediatRPatternUncertainties(ContextCapsule capsule, IEnumerable<AnnotationRecord> patterns)
    {
        foreach (var group in patterns.GroupBy(annotation => annotation.Value, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var ifaceName = group.Key;
            var symbolIds = group.Select(annotation => annotation.SymbolId)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var boundaryId = ifaceName switch
            {
                "IStreamRequestHandler" or "IAsyncStreamHandler" => "mediatr_stream_handler",
                "IPipelineBehavior" => "mediatr_pipeline_behavior",
                "IRequestExceptionHandler" => "mediatr_exception_handler",
                "IRequestPreProcessor" or "IRequestPostProcessor" => "mediatr_pre_post_processor",
                _ => null
            };
            var reason = boundaryId is not null
                ? DeclaredBoundaries.FindById(boundaryId)?.UncertaintyReason
                  ?? $"MediatR pattern '{ifaceName}' is not modeled: the implementing type was detected but no edge was emitted."
                : $"MediatR pattern '{ifaceName}' is not modeled: the implementing type was detected but no edge was emitted.";
            capsule.Uncertainties.Add(new UncertaintyEntry(symbolIds, "unmodeled_mediatr_pattern", reason));
        }
    }

    internal void PopulateSuggestedVerification(ContextCapsule capsule)
    {
        // TestedBy direction is production -> test. Query outgoing edges from the
        // anchor production symbol and collect targets as suggested tests.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tests = new List<(string TestId, string? ClassId)>();
        foreach (var productionId in TestSymbolDiscovery.ExpandProductionSymbolIds(_symbolId.Value))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var outgoingEdges = _edgeStore.GetOutgoingEdges(_snapshotId, productionId);
            foreach (var edge in outgoingEdges)
            {
                if (edge.Kind != nameof(EdgeKind.TestedBy) || !seen.Add(edge.TargetSymbolId))
                    continue;

                tests.Add((edge.TargetSymbolId, SymbolId.DeriveContainingTypeSymbolId(edge.TargetSymbolId)));
            }
        }

        var testsPerClass = tests
            .Where(static test => test.ClassId != null)
            .GroupBy(static test => test.ClassId!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

        var emittedClasses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (testId, classId) in tests)
        {
            if (classId == null || testsPerClass[classId] == 1)
            {
                AddTestSuggestion(capsule, testId);
                continue;
            }

            if (emittedClasses.Add(classId))
                AddClassSuggestion(capsule, classId);
        }

        var matchedProjects = AllCapsuleItems(capsule)
            .Select(MatchProjectKey)
            .Where(static key => key != null)
            .Distinct(ProjectFacts.AssemblyNameComparer)
            .ToList();
        if (matchedProjects.Count >= 3)
        {
            var solution = ResolveSnapshotSolutionPath();
            var command = solution == null ? "dotnet test" : $"dotnet test \"{solution}\"";
            capsule.SuggestedVerification.Add(new VerificationSuggestion(
                "full-suite",
                "Full suite",
                "Run the full suite because the change crosses three or more project boundaries.",
                command,
                "multi_project_blast_radius"));
        }
    }

    private void AddTestSuggestion(ContextCapsule capsule, string testId)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var testInfo = _declarationStore.GetSymbolInfo(testId, _snapshotId);
        var testName = testInfo?.FullyQualifiedName ?? testId;
        var targetPath = ResolveProjectPath(testId) ?? ResolveSnapshotSolutionPath();
        var normalizedTestName = FqnNormalizer.NormalizeForCommand(testName);
        var command = targetPath == null
            ? $"dotnet test --filter \"FullyQualifiedName={normalizedTestName}\""
            : $"dotnet test \"{targetPath}\" --filter \"FullyQualifiedName={normalizedTestName}\"";

        capsule.SuggestedVerification.Add(new VerificationSuggestion(
            testId,
            testName,
            $"Run '{testName}' to verify correctness after modifications.",
            command));
    }

    private void AddClassSuggestion(ContextCapsule capsule, string classId)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var className = _declarationStore.GetSymbolInfo(classId, _snapshotId)?.FullyQualifiedName;
        if (string.IsNullOrEmpty(className))
            className = DeriveTypeName(classId);
        var targetPath = ResolveProjectPath(classId) ?? ResolveSnapshotSolutionPath();
        var normalizedClassName = FqnNormalizer.NormalizeForCommand(className);
        var command = targetPath == null
            ? $"dotnet test --filter \"FullyQualifiedName~{normalizedClassName}\""
            : $"dotnet test \"{targetPath}\" --filter \"FullyQualifiedName~{normalizedClassName}\"";

        capsule.SuggestedVerification.Add(new VerificationSuggestion(
            classId,
            className,
            $"Run '{className}' to verify correctness after modifications.",
            command));
    }

    private static string DeriveTypeName(string classId)
    {
        return SymbolId.TryParse(classId, out var parsed)
               && parsed.DocCommentId.StartsWith("T:", StringComparison.Ordinal)
            ? parsed.DocCommentId["T:".Length..]
            : classId;
    }

    private static IEnumerable<CapsuleItem> AllCapsuleItems(ContextCapsule capsule)
    {
        return capsule.Contracts
            .Concat(capsule.DirectCallees)
            .Concat(capsule.DirectCallers)
            .Concat(capsule.RegisteredImplementations)
            .Concat(capsule.RelevantTests)
            .Concat(capsule.SecondDegreeContext)
            .Concat(capsule.SurroundingSource);
    }

    private string? MatchProjectKey(CapsuleItem item)
    {
        if (_snapshotMetadata == null || !SymbolId.TryParse(item.SymbolId, out var parsed))
            return null;

        var assemblyName = MetadataReferenceIdentity.SimpleName(parsed.AssemblyIdentity);
        foreach (var project in _snapshotMetadata.Projects)
        {
            var projectAssembly = ProjectFacts.GetAssemblyName(project);
            if (ProjectFacts.AssemblyNameComparer.Equals(projectAssembly, assemblyName))
                return projectAssembly;
        }

        return null;
    }

    private string? ResolveProjectPath(string symbolId)
    {
        if (_snapshotMetadata == null || !SymbolId.TryParse(symbolId, out var parsed))
            return null;

        var assemblyName = MetadataReferenceIdentity.SimpleName(parsed.AssemblyIdentity);
        string? matchedPath = null;
        foreach (var project in _snapshotMetadata.Projects)
        {
            if (!ProjectFacts.AssemblyNameComparer.Equals(ProjectFacts.GetAssemblyName(project), assemblyName))
                continue;
            if (string.IsNullOrEmpty(project.ProjectPath))
                return null;

            var normalized = PathNormalizer.ToForwardSlash(project.ProjectPath);
            if (matchedPath == null)
                matchedPath = normalized;
            else if (!PathNormalizer.PathComparer.Equals(matchedPath, normalized))
                return null;
        }

        return matchedPath;
    }

    private string? ResolveSnapshotSolutionPath()
    {
        if (_snapshotMetadata == null || string.IsNullOrWhiteSpace(_snapshotMetadata.SolutionPath))
            return null;

        var solution = PathNormalizer.ToForwardSlash(_snapshotMetadata.SolutionPath);
        if (string.IsNullOrWhiteSpace(_snapshotMetadata.GitRoot))
            return solution;

        var root = PathNormalizer.ToForwardSlash(_snapshotMetadata.GitRoot).TrimEnd('/');
        if (solution.StartsWith(root + "/", PathNormalizer.PathComparison))
            return solution[(root.Length + 1)..];

        return Path.IsPathRooted(_snapshotMetadata.SolutionPath)
            ? PathNormalizer.ToGitRelative(_snapshotMetadata.SolutionPath, _snapshotMetadata.GitRoot)
            : solution;
    }
}
