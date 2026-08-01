using Lurp.Storage;

namespace Lurp.Workspace
{
    internal sealed record ContextLookup(
        string SnapshotId,
        string? SymbolArg,
        string? FileArg,
        int? LineNumber
    );

    internal sealed record ContextAssemblyOptions(
        ContextIntent Intent,
        int Budget,
        int MaxHops = 3,
        bool IncludeGenerated = false,
        string? Scope = null,
        IReadOnlyList<string>? AffectedProjects = null,
        string? ChangeObjective = null,
        IReadOnlyList<string>? CallerConstraints = null,
        IReadOnlyList<ImpactPath>? TargetTopology = null,
        IReadOnlyList<string>? TopologyAnnotations = null,
        string? GitRoot = null
    );

    internal sealed class ContextAssembler
    {
        private readonly HashSet<string> _changeSiteKeys = new(StringComparer.Ordinal);

        public IEdgeStore EdgeStore { get; init; } = null!;
        public IDeclarationStore DeclarationStore { get; init; } = null!;
        public string SnapshotId { get; init; } = string.Empty;
        public SymbolId SymbolId { get; init; } = null!;
        public ContextIntent Intent { get; init; }
        public int Budget { get; init; }
        public int MaxHops { get; init; } = 3;
        public bool IncludeGenerated { get; init; }
        public string? Scope { get; init; }
        public IReadOnlyList<string> AffectedProjects { get; init; } = [];
        public string? ChangeObjective { get; init; }
        public IReadOnlyList<string> CallerConstraints { get; init; } = [];
        public IReadOnlyList<ImpactPath> TargetTopology { get; init; } = [];
        public IReadOnlyList<string> TopologyAnnotations { get; init; } = [];
        public string? GitRoot { get; init; }

        public ContextCapsule Assemble()
        {
            var context = new ContextTierContext(EdgeStore, DeclarationStore, SnapshotId, SymbolId, MaxHops, IncludeGenerated);
            var anchor = BuildAnchor();
            var capsule = new ContextCapsule(anchor)
            {
                Budget = Budget,
            };

            var runningTotal = ContextBudgeter.Apply(capsule, GetTierBuilders(context), Budget, EstimateTokens(anchor.Source));
            capsule.EstimatedTokens = runningTotal;

            PopulateContractSections(capsule);

            new UncertaintyDetector(EdgeStore, DeclarationStore, SnapshotId, SymbolId, IncludeGenerated, GitRoot)
                .Detect(capsule);

            return capsule;
        }

        private void PopulateContractSections(ContextCapsule capsule)
        {
            capsule.InclusionReasons["contracts"] = "Compiler-resolved contracts implemented or overridden by the anchor.";
            capsule.InclusionReasons["directCallees"] = "Direct compiler-resolved calls or constructions made by the anchor.";
            capsule.InclusionReasons["directCallers"] = "Direct callers and framework entry points that can reach the anchor.";
            capsule.InclusionReasons["registeredImplementations"] = "Persisted dispatch, registration, or handler targets relevant at runtime.";
            capsule.InclusionReasons["relevantTests"] = "Persisted TestedBy evidence connected to the anchor or its upstream callers.";
            capsule.InclusionReasons["secondDegreeContext"] = "Bounded upstream paths within the requested hop limit.";
            capsule.InclusionReasons["surroundingSource"] = "Sibling declarations sharing the anchor's containing declaration.";

            var traverser = new ImpactTraverser(EdgeStore, SnapshotId);
            capsule.IncomingPaths.AddRange(traverser.TraceImpact(SymbolId.Value, ImpactDirection.Upstream, maxDepth: MaxHops));
            capsule.OutgoingPaths.AddRange(traverser.TraceImpact(SymbolId.Value, ImpactDirection.Downstream, maxDepth: MaxHops));

            foreach (var annotation in EdgeStore.GetAnnotations(SnapshotId))
            {
                if (annotation.Kind.Contains("constraint", StringComparison.OrdinalIgnoreCase) ||
                    annotation.Kind.Contains("invariant", StringComparison.OrdinalIgnoreCase))
                {
                    capsule.Constraints.Add(new CapsuleConstraint(annotation.Value, "annotation", annotation.Kind, annotation.SymbolId));
                }
            }
            capsule.Constraints.AddRange(CallerConstraints.Select(value => new CapsuleConstraint(value, "caller_supplied")));
            var topologyAnnotations = TopologyAnnotations
                .Select(value => new CapsuleConstraint(value, "caller_supplied"))
                .ToList();
            capsule.Topology = new CapsuleTopology(
                capsule.IncomingPaths.Concat(capsule.OutgoingPaths).ToList(),
                TargetTopology.ToList(),
                topologyAnnotations);

            AddChangeSites(capsule, [capsule.Anchor.Locations], "anchor", 0, capsule.Anchor.SymbolId);
            AddItemChangeSites(capsule, capsule.DirectCallers, "direct caller", 1);
            AddItemChangeSites(capsule, capsule.RegisteredImplementations, "composition point", 2);
            capsule.LikelyChangeSites.Sort(static (left, right) =>
            {
                var rankComparison = left.Rank.CompareTo(right.Rank);
                return rankComparison != 0 ? rankComparison : string.Compare(left.Path, right.Path, StringComparison.Ordinal);
            });

            var candidates = new List<CapsuleItem>();
            candidates.AddRange(capsule.Contracts);
            candidates.AddRange(capsule.DirectCallers);
            candidates.AddRange(capsule.RegisteredImplementations);
            foreach (var candidate in candidates.Prepend(new CapsuleItem(
                         capsule.Anchor.SymbolId, capsule.Anchor.Kind, capsule.Anchor.FullyQualifiedName,
                         capsule.Anchor.Provenance, "anchor", capsule.Anchor.Source,
                         capsule.Anchor.Locations.FirstOrDefault(), "anchor declaration")))
            {
                var metadata = DeclarationStore.GetSymbolInfo(candidate.SymbolId, SnapshotId)?.MetadataJson;
                if (IsPublicSurface(metadata) && capsule.AffectedPublicSurfaces.All(item => item.SymbolId != candidate.SymbolId))
                    capsule.AffectedPublicSurfaces.Add(candidate);
            }
            if (capsule.AffectedPublicSurfaces.Count == 0)
            {
                // AffectedPublicSurfaces is not a budgeter tier, so record its
                // emptiness through the same reason-coded omission channel the
                // budgeter uses for empty tiers.
                capsule.OmittedTiers.Add(new TruncationEntry("affectedPublicSurfaces", "empty"));
            }

            if (EdgeStore is IBindingIncompletenessStore bindingStore)
            {
                capsule.Completeness = new SnapshotCompleteness
                {
                    ExtractorVersion = VersionConstants.ExtractorVersion,
                    BindingIncompleteness = bindingStore.GetBindingIncompleteness(SnapshotId),
                };
            }
        }

        private static bool IsPublicSurface(string? metadataJson)
        {
            if (string.IsNullOrWhiteSpace(metadataJson))
                return false;
            using var document = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (!document.RootElement.TryGetProperty(SymbolMetadataKeys.Accessibility, out var accessibility))
                return false;
            return accessibility.GetString() is "Public" or "Protected" or "ProtectedOrInternal";
        }

        private void AddItemChangeSites(ContextCapsule capsule, IEnumerable<CapsuleItem> items, string role, int rank)
        {
            foreach (var item in items)
            {
                if (item.DocumentPath != null)
                    AddChangeSite(capsule, item.DocumentPath, rank, role, item.SymbolId);
            }
        }

        private void AddChangeSites(ContextCapsule capsule, IEnumerable<List<DeclarationLocation>> locationGroups, string role, int rank, string symbolId)
        {
            foreach (var location in locationGroups.SelectMany(static locations => locations))
                AddChangeSite(capsule, location.DocumentPath, rank, role, symbolId);
        }

        private void AddChangeSite(ContextCapsule capsule, string path, int rank, string role, string symbolId)
        {
            if (!_changeSiteKeys.Add($"{path}\u0001{role}\u0001{symbolId}"))
                return;
            capsule.LikelyChangeSites.Add(new LikelyChangeSite(path, rank, role, symbolId));
        }

        private List<IContextTierBuilder> GetTierBuilders(ContextTierContext context)
        {
            IContextTierBuilder contracts = new ContractsTierBuilder(context);
            IContextTierBuilder directCallees = new DirectCalleesTierBuilder(context);
            IContextTierBuilder directCallers = new DirectCallersTierBuilder(context);
            IContextTierBuilder registeredImplementations = new RegisteredImplementationsTierBuilder(context);
            IContextTierBuilder relevantTests = new RelevantTestsTierBuilder(context);
            IContextTierBuilder secondDegreeContext = new SecondDegreeContextTierBuilder(context);
            IContextTierBuilder surroundingSiblings = new SurroundingSiblingsTierBuilder(context);

            return Intent switch
            {
                ContextIntent.Inspect =>
                [
                    contracts, directCallees, directCallers, registeredImplementations,
                    relevantTests, secondDegreeContext, surroundingSiblings,
                ],

                ContextIntent.Modify =>
                [
                    contracts, directCallers, registeredImplementations, relevantTests,
                    directCallees, secondDegreeContext, surroundingSiblings,
                ],

                ContextIntent.Diagnose =>
                [
                    directCallers, registeredImplementations, contracts, directCallees,
                    relevantTests, secondDegreeContext, surroundingSiblings,
                ],

                _ =>
                [
                    contracts, directCallees, directCallers, registeredImplementations,
                    relevantTests, secondDegreeContext, surroundingSiblings,
                ],
            };
        }

        internal static void AddTierToCapsule(ContextCapsule capsule, string tierName, List<CapsuleItem> items)
        {
            switch (tierName)
            {
                case "contracts":
                    capsule.Contracts.AddRange(items);
                    break;
                case "directCallees":
                    capsule.DirectCallees.AddRange(items);
                    break;
                case "directCallers":
                    capsule.DirectCallers.AddRange(items);
                    break;
                case "registeredImplementations":
                    capsule.RegisteredImplementations.AddRange(items);
                    break;
                case "relevantTests":
                    capsule.RelevantTests.AddRange(items);
                    break;
                case "secondDegreeContext":
                    capsule.SecondDegreeContext.AddRange(items);
                    break;
                case "surroundingSource":
                    capsule.SurroundingSource.AddRange(items);
                    break;
            }
        }

        private CapsuleAnchor BuildAnchor()
        {
            var info = DeclarationStore.GetSymbolInfo(SymbolId.Value, SnapshotId);
            if (info == null)
            {
                throw new InvalidOperationException($"Symbol '{SymbolId.Value}' not found in snapshot '{SnapshotId}'.");
            }

            var source = DeclarationStore.GetSymbolSource(SymbolId.Value, SnapshotId, ViewKind.Declaration, IncludeGenerated);
            source ??= string.Empty;

            return new CapsuleAnchor(symbolId: SymbolId.Value, fullyQualifiedName: info.FullyQualifiedName ?? SymbolId.Value, kind: info.Kind.ToString(),
                source: source)
            {
                Scope = Scope ?? SymbolId.Value,
                Intent = Intent.ToString().ToLowerInvariant(),
                MaxHops = MaxHops,
                SnapshotId = SnapshotId,
                AffectedProjects = AffectedProjects.Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToList(),
                ChangeObjective = ChangeObjective,
                Provenance = Provenance.CompilerProved,
                ExtractorIdentity = VersionConstants.ExtractorVersion,
                Locations = DeclarationStore.GetDeclarationLocations(SymbolId.Value, SnapshotId, IncludeGenerated),
            };
        }

        internal static int EstimateTokens(string? text)
        {
            return (text ?? string.Empty).Length / 4;
        }

        public static ContextCapsule ResolveAndAssemble(IEdgeStore edgeStore, IDeclarationStore declarationStore, ContextLookup lookup, ContextAssemblyOptions options)
        {
            if (!string.IsNullOrEmpty(lookup.SymbolArg))
            {
                var symbolId = SymbolId.Parse(lookup.SymbolArg!);
                var assembler = new ContextAssembler
                {
                    EdgeStore = edgeStore,
                    DeclarationStore = declarationStore,
                    SnapshotId = lookup.SnapshotId,
                    SymbolId = symbolId,
                    Intent = options.Intent,
                    Budget = options.Budget,
                    MaxHops = options.MaxHops,
                    IncludeGenerated = options.IncludeGenerated,
                    Scope = options.Scope,
                    AffectedProjects = options.AffectedProjects ?? [],
                    ChangeObjective = options.ChangeObjective,
                    CallerConstraints = options.CallerConstraints ?? [],
                    TargetTopology = options.TargetTopology ?? [],
                    TopologyAnnotations = options.TopologyAnnotations ?? [],
                    GitRoot = options.GitRoot,
                };
                return assembler.Assemble();
            }

            var resolvedId = declarationStore.ResolveSymbolByLocation(lookup.FileArg!, lookup.LineNumber!.Value, lookup.SnapshotId, options.IncludeGenerated);

            if (resolvedId == null)
            {
                var gapAnchor = new CapsuleAnchor(
                    symbolId: $"file://{lookup.FileArg}:{lookup.LineNumber}",
                    fullyQualifiedName: $"<no symbol at {lookup.FileArg}:{lookup.LineNumber}>",
                    kind: "gap",
                    source: string.Empty);

                var gapCapsule = new ContextCapsule(gapAnchor)
                {
                    Budget = options.Budget,
                    EstimatedTokens = 0,
                    Truncated = false,
                };

                gapCapsule.Uncertainties.Add(new UncertaintyEntry(
                    [gapAnchor.SymbolId],
                    "location_gap",
                    $"No symbol found at {lookup.FileArg}:{lookup.LineNumber}. The location may be in a comment, whitespace, or within a region not represented in the index."));

                return gapCapsule;
            }

            var resolvedSymbolId = SymbolId.Parse(resolvedId);
            var resolvedAssembler = new ContextAssembler
            {
                EdgeStore = edgeStore,
                DeclarationStore = declarationStore,
                SnapshotId = lookup.SnapshotId,
                SymbolId = resolvedSymbolId,
                Intent = options.Intent,
                Budget = options.Budget,
                MaxHops = options.MaxHops,
                IncludeGenerated = options.IncludeGenerated,
                Scope = options.Scope,
                AffectedProjects = options.AffectedProjects ?? [],
                ChangeObjective = options.ChangeObjective,
                CallerConstraints = options.CallerConstraints ?? [],
                TargetTopology = options.TargetTopology ?? [],
                TopologyAnnotations = options.TopologyAnnotations ?? [],
                GitRoot = options.GitRoot,
            };
            return resolvedAssembler.Assemble();
        }
    }

    internal static class ContextBudgeter
    {
        internal static int Apply(ContextCapsule capsule, IEnumerable<IContextTierBuilder> tiers, int budget, int runningTotal)
        {
            var truncatedCategories = new List<string>();
            var omittedTiers = new List<TruncationEntry>();
            var budgetExhausted = runningTotal > budget;

            foreach (var tier in tiers)
            {
                var items = tier.Build();
                if (items.Count == 0)
                {
                    omittedTiers.Add(new TruncationEntry(tier.Name, "empty"));
                    continue;
                }
                if (budgetExhausted)
                {
                    truncatedCategories.Add(tier.Name);
                    omittedTiers.Add(new TruncationEntry(tier.Name, "budget_exhausted"));
                    continue;
                }

                var tierCost = items.Sum(item => ContextAssembler.EstimateTokens(item.Source));
                if (runningTotal + tierCost <= budget)
                {
                    ContextAssembler.AddTierToCapsule(capsule, tier.Name, items);
                    runningTotal += tierCost;
                    continue;
                }

                // Deliberate greedy-prefix policy: preserve the builder's relevance
                // order and never let a lower-priority item or tier leapfrog the
                // first item that cannot fit.
                foreach (var item in items)
                {
                    var itemCost = ContextAssembler.EstimateTokens(item.Source);
                    if (runningTotal + itemCost > budget)
                    {
                        budgetExhausted = true;
                        break;
                    }
                    ContextAssembler.AddTierToCapsule(capsule, tier.Name, [item]);
                    runningTotal += itemCost;
                }
                truncatedCategories.Add(tier.Name);
                omittedTiers.Add(new TruncationEntry(tier.Name, "budget_exhausted"));
            }

            capsule.Truncated = truncatedCategories.Count > 0;
            capsule.TruncatedCategories = truncatedCategories;
            capsule.OmittedTiers = omittedTiers;
            return runningTotal;
        }
    }
}
