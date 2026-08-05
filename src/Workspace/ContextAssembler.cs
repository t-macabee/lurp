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
        string? GitRoot = null,
        bool IncludeCompletenessDetail = false
    );

    internal sealed class ContextAssembler
    {
        private readonly HashSet<string> _changeSiteKeys = new(StringComparer.Ordinal);

        public IEdgeStore EdgeStore { get; init; } = null!;
        public IDeclarationStore DeclarationStore { get; init; } = null!;

        /// <summary>
        /// Optional explicit completeness reader. When it is absent, snapshot
        /// completeness is unavailable and no relation omission may be reported
        /// as a proved "empty": every empty tier is marked "unresolved" instead,
        /// because without the reader no absence can be observed.
        /// </summary>
        public IBindingIncompletenessStore? BindingIncompletenessStore { get; init; }

        /// <summary>
        /// Optional reader for the persisted snapshot completeness (active
        /// TFMs, skipped adapters, extractor version). Absent only in tests
        /// that construct a capsule without a backing snapshot store.
        /// </summary>
        public ISnapshotStore? SnapshotStore { get; init; }

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
        public bool IncludeCompletenessDetail { get; init; }

        public ContextCapsule Assemble()
        {
            var context = new ContextTierContext(EdgeStore, DeclarationStore, SnapshotId, SymbolId, MaxHops, IncludeGenerated);
            var anchor = BuildAnchor();
            var capsule = new ContextCapsule(anchor)
            {
                Budget = Budget,
            };

            var bindingIncompleteness = BindingIncompletenessStore?.GetBindingIncompleteness(SnapshotId) ?? [];

            // Whether the anchor sits in a region where bindings were lost decides how an
            // empty tier is reported. Resolved here so the budgeter can label emptiness
            // as "unresolved" rather than as a proved "empty". When the completeness
            // reader itself is absent, no absence can be observed at all, so the region
            // is treated as unobservable: absence of the reader must never become a
            // proved "empty" relation.
            var anchorBindingIsIncomplete = BindingIncompletenessStore == null
                || AnchorRegionHasLostBindings(anchor, bindingIncompleteness);

            // Non-tier sections (paths, topology, constraints, completeness
            // summary, inclusion reasons) are populated after the tier budgeter
            // runs. Reserve a fraction of the budget so the tier-level
            // greedy-prefix decisions do not exhaust the budget before those
            // sections are added : the CapsuleBudgetEnforcer re-measures the
            // whole artifact afterward, but the tier *selection* quality improves
            // when the budgeter knows its effective headroom.
            var nonTierReserve = Math.Min(Budget / 4, 500);
            var tiers = GetTierBuilders(context);
            var runningTotal = ContextBudgeter.Apply(capsule, tiers, Budget - nonTierReserve,
                EstimateTokens(anchor.Source), anchorBindingIsIncomplete);
            capsule.EstimatedTokens = runningTotal;

            PopulateContractSections(capsule, bindingIncompleteness, anchorBindingIsIncomplete);

            new UncertaintyDetector(EdgeStore, DeclarationStore, SnapshotId, SymbolId, IncludeGenerated, GitRoot, bindingIncompleteness)
                .Detect(capsule);

            // The tier budgeter bounds tier item source; this final pass measures the
            // emitted capsule representation itself and trims/summarizes every
            // consumer-visible section (paths, topology, completeness, constraints,
            // uncertainties, verification, ...) against the same estimator.
            CapsuleBudgetEnforcer.Enforce(capsule, Budget, tiers.Select(static tier => tier.Name).ToList());

            return capsule;
        }

        private void PopulateContractSections(ContextCapsule capsule, IReadOnlyList<BindingIncompletenessRecord> bindingIncompleteness, bool anchorBindingIsIncomplete)
        {
            capsule.InclusionReasons["contracts"] = "Compiler-resolved contracts implemented or overridden by the anchor.";
            capsule.InclusionReasons["directCallees"] = "Direct compiler-resolved calls or constructions made by the anchor.";
            capsule.InclusionReasons["directCallers"] = "Direct callers and framework entry points that can reach the anchor.";
            capsule.InclusionReasons["registeredImplementations"] = "Persisted dispatch, registration, or handler targets relevant at runtime.";
            capsule.InclusionReasons["relevantTests"] = "Persisted TestedBy evidence connected to the anchor or its upstream callers.";
            capsule.InclusionReasons["secondDegreeContext"] = "Bounded upstream paths within the requested hop limit.";
            capsule.InclusionReasons["surroundingSource"] = "Sibling declarations sharing the anchor's containing declaration.";

            // A budget_exhausted omission is only honest if it is also actionable, so the
            // capsule states the continuation in-band: a consumer holding just this capsule
            // can recover the omitted section without widening the budget and re-reading
            // everything it already has.
            //
            // Emitted unconditionally and kept terse. Conditioning it on an omission having
            // already happened would miss the ones CapsuleBudgetEnforcer adds later (it runs
            // after this method), and moving it after the enforcer would leave its own cost
            // outside the measurement that estimatedTokens reports. One short line, always
            // present, always counted, is the version with no gap between the two.
            capsule.InclusionReasons["omittedTiers.budget_exhausted"] =
                "Fetch an omitted tier on its own, unbudgeted: --mode=context --tier=<category> "
              + "--symbol=<anchor symbolId> [--cursor=<next_cursor>].";

            if (anchorBindingIsIncomplete)
            {
                capsule.InclusionReasons["omittedTiers.unresolved"] =
                    "Bindings were lost over the anchor's documents, so an omitted tier marked "
                  + "'unresolved' means the relation could not be observed. It is NOT evidence that "
                  + "no such relation exists. Only tiers marked 'empty' are a proved absence.";
            }

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

            // The current topology is the union of incomingPaths and outgoingPaths.
            // Those collections are serialized once above; the reference summary
            // preserves the topology meaning (direction, path and hop counts)
            // without duplicating the path data.
            var totalHops = capsule.IncomingPaths.Sum(path => path.Hops.Count)
                + capsule.OutgoingPaths.Sum(path => path.Hops.Count);
            capsule.Topology = new CapsuleTopology(
                new CapsuleTopologyReference(
                    "see incomingPaths",
                    "see outgoingPaths",
                    capsule.IncomingPaths.Count,
                    capsule.OutgoingPaths.Count,
                    totalHops),
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
                // budgeter uses for empty tiers, including the same proved-absence
                // versus unobservable-region distinction.
                var reason = anchorBindingIsIncomplete ? "unresolved" : "empty";
                capsule.OmittedTiers.Add(new TruncationEntry("affectedPublicSurfaces", reason));
            }

            if (BindingIncompletenessStore != null)
            {
                // Storage is the only producer of completeness; the hydrated
                // manifest completeness (TFMs, skipped adapters, ...) is
                // enriched with binding detail through the one method that
                // keeps detail, summary, and total together. Detailed
                // per-document rows are emitted only behind an explicit
                // detail option; the default is a deterministic
                // reason/project rollup that stays within a bounded size.
                var snapshotMetadata = SnapshotStore?.LoadSnapshotMetadata(SnapshotId);
                var baseCompleteness = (snapshotMetadata != null ? SnapshotManifest.FromStorageManifest(snapshotMetadata).Completeness : null)
                    ?? new SnapshotCompleteness { ExtractorVersion = VersionConstants.ExtractorVersion };
                capsule.Completeness = baseCompleteness.WithBindingIncompleteness(bindingIncompleteness, IncludeCompletenessDetail);
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

        /// <summary>
        /// Single registry of every tier a capsule can carry : the canonical name,
        /// the factory that builds its builder, and the capsule collection the
        /// builder's items are added to. <c>GetTierBuilders</c>, <c>ResolveTierBuilder</c>,
        /// <c>TierNames</c>, and <c>AddTierToCapsule</c> are all derived from this
        /// list, so adding a tier is exactly one edit here.
        /// </summary>
        private static readonly (string Name, Func<ContextTierContext, IContextTierBuilder> Factory, Func<ContextCapsule, List<CapsuleItem>> Collection)[] TierBuilders =
        [
            ("contracts", static context => new ContractsTierBuilder(context), static capsule => capsule.Contracts),
            ("directCallees", static context => new DirectCalleesTierBuilder(context), static capsule => capsule.DirectCallees),
            ("directCallers", static context => new DirectCallersTierBuilder(context), static capsule => capsule.DirectCallers),
            ("registeredImplementations", static context => new RegisteredImplementationsTierBuilder(context), static capsule => capsule.RegisteredImplementations),
            ("relevantTests", static context => new RelevantTestsTierBuilder(context), static capsule => capsule.RelevantTests),
            ("secondDegreeContext", static context => new SecondDegreeContextTierBuilder(context), static capsule => capsule.SecondDegreeContext),
            ("surroundingSource", static context => new SurroundingSiblingsTierBuilder(context), static capsule => capsule.SurroundingSource),
        ];

        private static readonly Dictionary<string, (Func<ContextTierContext, IContextTierBuilder> Factory, Func<ContextCapsule, List<CapsuleItem>> Collection)> TierBuilderLookup =
            TierBuilders.ToDictionary(entry => entry.Name, entry => (entry.Factory, entry.Collection), StringComparer.Ordinal);

        /// <summary>
        /// Every tier name a capsule can carry. Ordering here is presentation only :
        /// the assembly priority is intent-dependent and lives in <c>GetTierBuilders</c>.
        /// </summary>
        internal static readonly string[] TierNames =
            TierBuilders.Select(static entry => entry.Name).ToArray();

        private List<IContextTierBuilder> GetTierBuilders(ContextTierContext context)
        {
            return Intent switch
            {
                ContextIntent.Inspect => ResolveInOrder(context,
                    "contracts", "directCallees", "directCallers", "registeredImplementations",
                    "relevantTests", "secondDegreeContext", "surroundingSource"),

                ContextIntent.Modify => ResolveInOrder(context,
                    "contracts", "directCallers", "registeredImplementations", "relevantTests",
                    "directCallees", "secondDegreeContext", "surroundingSource"),

                ContextIntent.Diagnose => ResolveInOrder(context,
                    "directCallers", "registeredImplementations", "contracts", "directCallees",
                    "relevantTests", "secondDegreeContext", "surroundingSource"),

                _ => ResolveInOrder(context,
                    "contracts", "directCallees", "directCallers", "registeredImplementations",
                    "relevantTests", "secondDegreeContext", "surroundingSource"),
            };
        }

        private static List<IContextTierBuilder> ResolveInOrder(ContextTierContext context, params string[] tierNames)
            => tierNames.Select(name => ResolveTierBuilder(context, name)!).ToList();

        internal static IContextTierBuilder? ResolveTierBuilder(ContextTierContext context, string tierName)
            => TierBuilderLookup.TryGetValue(tierName, out var entry) ? entry.Factory(context) : null;

        /// <summary>
        /// Builds one tier on its own, outside the capsule budget, and returns a page of it.
        ///
        /// This exists because a capsule that reports a tier as <c>budget_exhausted</c>
        /// previously left the consumer no way to act on that admission except to widen the
        /// whole budget and re-read the entire capsule to recover one section. The tier is
        /// rebuilt from the same immutable snapshot by the same builder, so a page fetched
        /// this way is the same evidence the capsule would have carried : it is bounded by
        /// <paramref name="limit"/> rather than by the capsule's token budget, because the
        /// caller asked for exactly this one section.
        /// </summary>
        internal static CapsuleTierPage BuildTierPage(
            IEdgeStore edgeStore,
            IDeclarationStore declarationStore,
            string snapshotId,
            SymbolId symbolId,
            string tierName,
            int maxHops,
            bool includeGenerated,
            int offset,
            int limit)
        {
            var info = declarationStore.GetSymbolInfo(symbolId.Value, snapshotId)
                ?? throw new InvalidOperationException($"Symbol '{symbolId.Value}' not found in snapshot '{snapshotId}'.");

            var context = new ContextTierContext(edgeStore, declarationStore, snapshotId, symbolId, maxHops, includeGenerated);
            var builder = ResolveTierBuilder(context, tierName)
                ?? throw new ArgumentException($"Unknown tier '{tierName}'. Valid tiers: {string.Join(", ", TierNames)}.");

            var items = builder.Build();
            var page = items.Skip(offset).Take(limit).ToList();

            return new CapsuleTierPage(
                tierName,
                symbolId.Value,
                info.FullyQualifiedName ?? symbolId.Value,
                info.Kind.ToString(),
                items.Count,
                offset,
                page,
                offset + page.Count < items.Count);
        }

        internal static void AddTierToCapsule(ContextCapsule capsule, string tierName, List<CapsuleItem> items)
        {
            if (TierBuilderLookup.TryGetValue(tierName, out var entry))
                entry.Collection(capsule).AddRange(items);
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

        /// <summary>
        /// True when any document the anchor is declared in lost bindings during
        /// extraction. In that region an absent relation is unobservable rather than
        /// absent, so nothing may report it as a proved emptiness.
        /// </summary>
        internal static bool AnchorRegionHasLostBindings(
            CapsuleAnchor anchor, IReadOnlyList<BindingIncompletenessRecord> bindingIncompleteness)
        {
            if (bindingIncompleteness.Count == 0)
                return false;

            var anchorDocuments = anchor.Locations
                .Select(static location => location.DocumentPath)
                .Where(static path => !string.IsNullOrEmpty(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (anchorDocuments.Count == 0)
                return false;

            foreach (var record in bindingIncompleteness)
            {
                if (!BindingIncompletenessReason.UnobservableReasons.Contains(record.Reason))
                    continue;
                if (record.DocumentPath != null && anchorDocuments.Contains(record.DocumentPath))
                    return true;
            }

            return false;
        }

        public static ContextCapsule ResolveAndAssemble(IEdgeStore edgeStore, IDeclarationStore declarationStore, ContextLookup lookup, ContextAssemblyOptions options, IBindingIncompletenessStore? bindingIncompletenessStore = null, ISnapshotStore? snapshotStore = null)
        {
            if (!string.IsNullOrEmpty(lookup.SymbolArg))
            {
                var symbolId = SymbolId.Parse(lookup.SymbolArg!);
                var assembler = new ContextAssembler
                {
                    EdgeStore = edgeStore,
                    DeclarationStore = declarationStore,
                    BindingIncompletenessStore = bindingIncompletenessStore,
                    SnapshotStore = snapshotStore,
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
                    IncludeCompletenessDetail = options.IncludeCompletenessDetail,
                };
                return assembler.Assemble();
            }

            var resolvedId = declarationStore.ResolveSymbolByLocation(lookup.FileArg!, lookup.LineNumber!.Value, lookup.SnapshotId, options.IncludeGenerated);

            if (resolvedId == null)
            {
                // A gap capsule is a real capsule and obeys the same finalization
                // contract as any other. In particular its tiers are NOT bare `[]`:
                // under the capsule's own empty/unresolved semantics a bare `[]`
                // asserts a proved absence, and nothing was proved here : the anchor
                // itself could not be resolved. Every tier is therefore reason-coded
                // "unresolved", the snapshot that was consulted is recorded, the
                // anchor carries no evidence grade (it asserts the absence of a
                // symbol, so "compiler_proved" would be a false claim), and the
                // budget enforcer settles estimatedTokens the same way it does
                // everywhere else.
                var gapAnchor = new CapsuleAnchor(
                    symbolId: $"file://{lookup.FileArg}:{lookup.LineNumber}",
                    fullyQualifiedName: $"<no symbol at {lookup.FileArg}:{lookup.LineNumber}>",
                    kind: "gap",
                    source: string.Empty)
                {
                    SnapshotId = lookup.SnapshotId,
                    Intent = options.Intent.ToString().ToLowerInvariant(),
                    MaxHops = options.MaxHops,
                    Provenance = string.Empty,
                };

                var gapCapsule = new ContextCapsule(gapAnchor)
                {
                    Budget = options.Budget,
                };

                foreach (var tierName in TierNames)
                    gapCapsule.OmittedTiers.Add(new TruncationEntry(tierName, "unresolved"));

                gapCapsule.InclusionReasons["omittedTiers.unresolved"] =
                    "No symbol resolved at the requested location, so every tier is marked "
                  + "'unresolved': the relation could not be observed. It is NOT evidence that "
                  + "no such relation exists. Only tiers marked 'empty' are a proved absence.";

                gapCapsule.Uncertainties.Add(new UncertaintyEntry(
                    [gapAnchor.SymbolId],
                    "location_gap",
                    $"No symbol found at {lookup.FileArg}:{lookup.LineNumber}. The location may be in a comment, whitespace, or within a region not represented in the index."));

                CapsuleBudgetEnforcer.Enforce(gapCapsule, options.Budget, TierNames);

                return gapCapsule;
            }

            var resolvedSymbolId = SymbolId.Parse(resolvedId);
            var resolvedAssembler = new ContextAssembler
            {
                EdgeStore = edgeStore,
                DeclarationStore = declarationStore,
                BindingIncompletenessStore = bindingIncompletenessStore,
                SnapshotStore = snapshotStore,
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
                IncludeCompletenessDetail = options.IncludeCompletenessDetail,
            };
            return resolvedAssembler.Assemble();
        }
    }
}
