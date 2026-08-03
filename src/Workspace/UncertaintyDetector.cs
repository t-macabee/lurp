using Lurp.Storage;

namespace Lurp.Workspace
{
    internal sealed class UncertaintyDetector
    {
        private readonly IEdgeStore _edgeStore;
        private readonly IDeclarationStore _declarationStore;
        private readonly string _snapshotId;
        private readonly SymbolId _symbolId;
        private readonly bool _includeGenerated;
        private readonly string? _gitRoot;
        private readonly IReadOnlyList<BindingIncompletenessRecord> _bindingIncompleteness;
        private readonly Dictionary<string, string?> _owningProjectCache = new(StringComparer.OrdinalIgnoreCase);
        private string? _solutionPath;
        private bool _solutionPathResolved;

        public UncertaintyDetector(
            IEdgeStore edgeStore,
            IDeclarationStore declarationStore,
            string snapshotId,
            SymbolId symbolId,
            bool includeGenerated,
            string? gitRoot = null,
            IReadOnlyList<BindingIncompletenessRecord>? bindingIncompleteness = null)
        {
            _edgeStore = edgeStore;
            _declarationStore = declarationStore;
            _snapshotId = snapshotId;
            _symbolId = symbolId;
            _includeGenerated = includeGenerated;
            _gitRoot = gitRoot;
            _bindingIncompleteness = bindingIncompleteness ?? [];
        }

        public void Detect(ContextCapsule capsule)
        {
            PopulateUncertainties(capsule);
            PopulateSuggestedVerification(capsule);
        }

        private void PopulateUncertainties(ContextCapsule capsule)
        {
            var neighborhood = BuildNeighborhood(capsule);

            CollectReflectionUncertainties(capsule, neighborhood);
            CollectDispatchUncertainties(capsule, neighborhood);
            CollectMissingReceiverConstraintUncertainties(capsule, neighborhood);
            CollectFrameworkConventionUncertainties(capsule, neighborhood);
            CollectBindingIncompletenessUncertainties(capsule);

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

        private void CollectReflectionUncertainties(ContextCapsule capsule, HashSet<string> neighborhood)
        {
            foreach (var symbolId in neighborhood)
            {
                var edges = _edgeStore.GetIncomingEdges(_snapshotId, symbolId)
                    .Concat(_edgeStore.GetOutgoingEdges(_snapshotId, symbolId));

                foreach (var edge in edges)
                {
                    if (edge.Kind == EdgeKind.ReflectionNameCandidate.ToString())
                    {
                        capsule.Uncertainties.Add(new UncertaintyEntry([edge.SourceSymbolId, edge.TargetSymbolId], edge.Kind, $"Reflection name candidate: the string-based reference to '{edge.TargetSymbolId}' was matched by name. Verify that this reference correctly resolves at runtime."));
                    }
                    else if (edge.Kind == EdgeKind.ReflectionTargetUnknown.ToString())
                    {
                        capsule.Uncertainties.Add(new UncertaintyEntry([edge.SourceSymbolId, edge.TargetSymbolId], edge.Kind, "Unknown reflection target: the runtime target of this reflection call cannot be statically determined."));
                    }
                }
            }
        }

        private void CollectDispatchUncertainties(ContextCapsule capsule, HashSet<string> neighborhood)
        {
            foreach (var symbolId in neighborhood)
            {
                var outgoing = _edgeStore.GetOutgoingEdges(_snapshotId, symbolId);
                foreach (var edge in outgoing)
                {
                    if (edge.Kind != EdgeKind.MayDispatchTo.ToString())
                        continue;
                    if (edge.Provenance == Provenance.CompilerProved || edge.Provenance == Provenance.FrameworkDerived)
                        continue;

                    capsule.Uncertainties.Add(new UncertaintyEntry([edge.SourceSymbolId, edge.TargetSymbolId], edge.Kind, $"Dispatch candidate '{edge.TargetSymbolId}' was resolved with evidence level '{edge.Provenance}'. Manually verify that the runtime dispatch reaches the correct implementation."));
                }
            }
        }

        private void CollectMissingReceiverConstraintUncertainties(ContextCapsule capsule, HashSet<string> neighborhood)
        {
            var seen = new HashSet<(string Source, string Target)>();
            foreach (var symbolId in neighborhood)
            {
                foreach (var call in _edgeStore.GetOutgoingEdges(_snapshotId, symbolId))
                {
                    if (call.Kind != EdgeKind.Calls.ToString() ||
                        ReceiverTypeConstraints.Deserialize(call.ReceiverTypeConstraintsJson).Count > 0 ||
                        !seen.Add((call.SourceSymbolId, call.TargetSymbolId)))
                    {
                        continue;
                    }

                    var hasDispatchRelation = _edgeStore.GetOutgoingEdges(_snapshotId, call.TargetSymbolId)
                        .Any(edge => edge.Kind == EdgeKind.MayDispatchTo.ToString());
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

        private void CollectFrameworkConventionUncertainties(ContextCapsule capsule, HashSet<string> neighborhood)
        {
            var frameworkKinds = new HashSet<string>
            {
                EdgeKind.RoutesTo.ToString(),
                EdgeKind.Handles.ToString(),
                EdgeKind.Registers.ToString()
            };

            foreach (var symbolId in neighborhood)
            {
                var edges = _edgeStore.GetIncomingEdges(_snapshotId, symbolId)
                    .Concat(_edgeStore.GetOutgoingEdges(_snapshotId, symbolId));

                foreach (var edge in edges)
                {
                    if (!frameworkKinds.Contains(edge.Kind))
                        continue;
                    if (edge.Provenance != Provenance.Convention)
                        continue;

                    capsule.Uncertainties.Add(new UncertaintyEntry([edge.SourceSymbolId, edge.TargetSymbolId], edge.Kind, $"Convention-based framework binding: the '{edge.Kind}' edge was inferred by naming convention, not explicit registration. Verify that the expected target is reached at runtime."));
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
                capsule.Uncertainties.Add(new UncertaintyEntry(
                    [_symbolId.Value],
                    "binding_incompleteness",
                    DescribeBindingIncompleteness(group.Key, count, projects)));
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
            {
                if (hop.SourceDocument != null)
                    paths.Add(hop.SourceDocument);
            }
            return paths;
        }

        private static void AddItemDocumentPaths(HashSet<string> paths, IEnumerable<CapsuleItem> items)
        {
            foreach (var item in items)
            {
                if (item.DocumentPath != null)
                    paths.Add(item.DocumentPath);
            }
        }

        private static string DescribeBindingIncompleteness(string reason, int count, IReadOnlyList<string> projects)
        {
            var scope = string.Join(", ", projects);
            return reason switch
            {
                BindingIncompletenessReason.CompilerError =>
                    $"{count} binding(s) in {scope} could not be completed because the snapshot compilation reported compiler errors in those projects. Relations that depend on that code may be missing from the graph even though the references exist in source.",
                BindingIncompletenessReason.UnresolvedMetadata =>
                    $"{count} binding(s) in {scope} could not be resolved against project metadata (for example missing package or project references). Relations that depend on those bindings may not be persisted even though the references exist in source.",
                BindingIncompletenessReason.FilteredExternal =>
                    $"{count} binding(s) in {scope} resolved to symbols in assemblies outside the compilation. Edges to those external targets are intentionally filtered from the persisted graph; their absence is a declared boundary, not an extraction failure.",
                BindingIncompletenessReason.AmbiguousOverload =>
                    $"{count} binding(s) in {scope} were ambiguous, so no unique overload target could be selected. Dispatch targets for those call sites are uncertain.",
                BindingIncompletenessReason.UnsupportedSyntax =>
                    $"{count} binding(s) in {scope} could not be completed because the extractor does not support the relevant syntax. Relations at those sites may be missing.",
                BindingIncompletenessReason.ExtractorFailure =>
                    $"{count} extractor failure(s) were recorded while producing the snapshot for {scope}. Some relations may be missing.",
                _ =>
                    $"{count} binding-incompleteness record(s) (reason '{reason}') affect {scope}. Relations in that code may be incomplete.",
            };
        }

        private void CollectGeneratedExclusionUncertainties(ContextCapsule capsule, HashSet<string> neighborhood)
        {
            foreach (var symbolId in neighborhood)
            {
                var hasGeneratedSource = _declarationStore.GetSymbolSource(symbolId, _snapshotId, ViewKind.Declaration, true);
                var hasNonGeneratedSource = _declarationStore.GetSymbolSource(symbolId, _snapshotId, ViewKind.Declaration, false);

                if (hasGeneratedSource != null && hasNonGeneratedSource == null)
                {
                    capsule.Uncertainties.Add(new UncertaintyEntry([symbolId], "generated_excluded", $"Generated symbol '{symbolId}' was excluded because includeGenerated is set to false. Review generated code if runtime behavior depends on it."));
                }
            }
        }

        private void PopulateSuggestedVerification(ContextCapsule capsule)
        {
            // TestedBy direction is production -> test. Query outgoing edges from the
            // anchor production symbol and collect targets as suggested tests.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var productionId in TestSymbolDiscovery.ExpandProductionSymbolIds(_symbolId.Value))
            {
                var outgoingEdges = _edgeStore.GetOutgoingEdges(_snapshotId, productionId);
                foreach (var edge in outgoingEdges)
                {
                    if (edge.Kind != EdgeKind.TestedBy.ToString() || !seen.Add(edge.TargetSymbolId))
                        continue;

                    var testId = edge.TargetSymbolId;
                    var testInfo = _declarationStore.GetSymbolInfo(testId, _snapshotId);
                    var testName = testInfo?.FullyQualifiedName ?? testId;
                    var projectPath = ResolveOwningProject(testId);
                    var command = projectPath == null
                        ? $"dotnet test --filter \"FullyQualifiedName={testName}\""
                        : $"dotnet test \"{projectPath}\" --filter \"FullyQualifiedName={testName}\"";

                    capsule.SuggestedVerification.Add(new VerificationSuggestion(
                        testId: testId,
                        testName: testName,
                        description: $"Run '{testName}' to verify correctness after modifications.",
                        command: command));
                }
            }

            var projects = AllCapsuleItems(capsule)
                .Select(item => ResolveOwningProject(item.SymbolId))
                .Where(static path => path != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (projects.Count >= 3)
            {
                capsule.SuggestedVerification.Clear();
                var solution = ResolveSolutionPath();
                var command = solution == null ? "dotnet test" : $"dotnet test \"{solution}\"";
                capsule.SuggestedVerification.Add(new VerificationSuggestion(
                    "full-suite",
                    "Full suite",
                    "Run the full suite because the change crosses three or more project boundaries.",
                    command,
                    "multi_project_blast_radius"));
            }
        }

        private static IEnumerable<CapsuleItem> AllCapsuleItems(ContextCapsule capsule)
            => capsule.Contracts
                .Concat(capsule.DirectCallees)
                .Concat(capsule.DirectCallers)
                .Concat(capsule.RegisteredImplementations)
                .Concat(capsule.RelevantTests)
                .Concat(capsule.SecondDegreeContext)
                .Concat(capsule.SurroundingSource);

        private string? ResolveOwningProject(string symbolId)
        {
            if (string.IsNullOrEmpty(_gitRoot))
                return null;
            var location = _declarationStore.GetDeclarationLocations(symbolId, _snapshotId, _includeGenerated).FirstOrDefault();
            if (location == null)
                return null;
            var root = Path.GetFullPath(_gitRoot);
            var directory = Path.GetDirectoryName(Path.Combine(root, location.DocumentPath));
            return ResolveProjectFromDirectory(directory, root);
        }

        private string? ResolveProjectFromDirectory(string? directory, string root)
        {
            if (string.IsNullOrEmpty(directory))
                return null;
            if (_owningProjectCache.TryGetValue(directory, out var cached))
                return cached;

            string? result = null;
            var current = directory;
            while (!string.IsNullOrEmpty(current) && current.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                var project = Directory.EnumerateFiles(current, "*.csproj", SearchOption.TopDirectoryOnly)
                    .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (project != null)
                {
                    result = Path.GetRelativePath(root, project).Replace('\\', '/');
                    break;
                }
                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                    break;
                current = Path.GetDirectoryName(current)!;
            }

            _owningProjectCache[current] = result;
            return result;
        }

        private string? ResolveSolutionPath()
        {
            if (_solutionPathResolved)
                return _solutionPath;
            _solutionPathResolved = true;

            if (string.IsNullOrEmpty(_gitRoot) || !Directory.Exists(_gitRoot))
                return null;
            var solution = Directory.EnumerateFiles(_gitRoot, "*.sln*", SearchOption.TopDirectoryOnly)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            _solutionPath = solution == null ? null : Path.GetRelativePath(_gitRoot, solution).Replace('\\', '/');
            return _solutionPath;
        }
    }
}
