using System.Text.Json;
using System.Text.Json.Serialization;
using Lurp.Storage;

namespace Lurp.Workspace
{
    // The tier budgeter bounds tier-item source against the same estimate; this
    // final pass applies the architecture's greedy priority policy across every
    // consumer-visible section. The budget measures CONTENT the agent consumes:
    // the source text of the anchor and every capsule item, plus the serialized
    // weight of the substantive non-source sections (paths, topology,
    // constraints, completeness, uncertainties, verification, likely change
    // sites, affected public surfaces, inclusion reasons). Per-item identity
    // framing (symbol ids, fully-qualified names, edge kinds, provenance,
    // coordinates) is navigation metadata and is not counted as content.
    //
    // Over-budget capsules first bound the path sections (a "summarized"
    // entry), then bound tier-item source text to a per-item cap, then clear the
    // lowest-priority sections greedily; every omitted/summarized category is
    // recorded in omittedTiers and truncatedCategories. The anchor is never
    // dropped; if it alone overflows the budget, that overflow is declared with
    // budget_exhausted. estimatedTokens is set to the settled content measure of
    // the capsule, so it always describes the emitted artifact's content within
    // the requested budget.
    internal static class CapsuleBudgetEnforcer
    {
        private const int MaxBoundPaths = 3;
        private const int MaxBoundHopsPerPath = 3;
        private const int MaxItemSourceChars = 800;
        private const string SourceTruncationMarker = "\n// … source truncated by token budget …";

        private static readonly JsonSerializerOptions SectionOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        internal static int Enforce(ContextCapsule capsule, int budget, IReadOnlyList<string> tierPriority)
        {
            var trimmer = new SectionTrimmer(capsule, tierPriority);
            var initialEstimate = Measure(capsule);
            if (initialEstimate <= budget)
            {
                capsule.EstimatedTokens = initialEstimate;
                return capsule.EstimatedTokens;
            }

            // The serialized path sections dominate a capsule and are the most
            // compressible content. Bound them first so that lower-priority
            // sections are not dropped merely to preserve an unbounded path blob.
            foreach (var category in (string[])["incomingPaths", "outgoingPaths"])
            {
                if (trimmer.BoundPathSection(category) is { } bounded)
                    RecordTruncation(capsule, bounded);
            }

            // Then bound tier-item source text. Keeping every required section
            // present (with bounded source) is preferred over dropping a whole
            // section; the greedy loop below clears sections only when bounding
            // no longer suffices.
            foreach (var name in tierPriority)
            {
                if (trimmer.BoundTierSources(name, MaxItemSourceChars) is { } bounded)
                    RecordTruncation(capsule, bounded);
            }

            while (true)
            {
                var estimate = Measure(capsule);
                capsule.EstimatedTokens = estimate;
                if (estimate <= budget)
                    return estimate;

                var entry = trimmer.TrimNextLowestPriority();
                if (entry == null)
                {
                    // Every trimmable section has been cleared and the capsule is
                    // still over budget. The anchor is priority 1 and is never
                    // dropped; declare the overflow rather than hiding it.
                    RecordTruncation(capsule, new TruncationEntry("anchor", "budget_exhausted"));
                    capsule.EstimatedTokens = Measure(capsule);
                    return capsule.EstimatedTokens;
                }
                RecordTruncation(capsule, entry);
            }
        }

        /// <summary>
        /// Content measure of the capsule: the source text of the anchor and
        /// every item, plus the serialized weight of the substantive non-source
        /// sections. Per-item identity framing is not counted.
        /// </summary>
        internal static int Measure(ContextCapsule capsule)
        {
            var chars = SourceChars(capsule.Anchor.Source);
            chars += SectionSourceChars(capsule.Contracts);
            chars += SectionSourceChars(capsule.DirectCallees);
            chars += SectionSourceChars(capsule.DirectCallers);
            chars += SectionSourceChars(capsule.RegisteredImplementations);
            chars += SectionSourceChars(capsule.RelevantTests);
            chars += SectionSourceChars(capsule.SecondDegreeContext);
            chars += SectionSourceChars(capsule.SurroundingSource);
            chars += SectionSourceChars(capsule.AffectedPublicSurfaces);
            chars += SerializedChars(capsule.IncomingPaths);
            chars += SerializedChars(capsule.OutgoingPaths);
            chars += SerializedChars(capsule.Constraints);
            chars += SerializedChars(capsule.InclusionReasons);
            chars += SerializedChars(capsule.LikelyChangeSites);
            chars += SerializedChars(capsule.Topology);
            chars += SerializedChars(capsule.Completeness);
            chars += SerializedChars(capsule.Uncertainties);
            chars += SerializedChars(capsule.SuggestedVerification);
            return chars / 4;
        }

        private static int SourceChars(string? text)
            => (text ?? string.Empty).Length;

        private static int SectionSourceChars(IEnumerable<CapsuleItem> items)
            => items.Sum(static item => item.Source?.Length ?? 0);

        private static int SerializedChars<T>(T value)
            => JsonSerializer.Serialize(value, SectionOptions).Length;

        private static void RecordTruncation(ContextCapsule capsule, TruncationEntry entry)
        {
            capsule.Truncated = true;
            if (!capsule.TruncatedCategories.Contains(entry.Category))
                capsule.TruncatedCategories.Add(entry.Category);
            capsule.OmittedTiers.Add(entry);
        }

        private static Func<ContextCapsule, bool> TopologyDropTargetsStep(ContextCapsule capsule)
            => _ =>
            {
                if (capsule.Topology.Target.Count == 0 && capsule.Topology.Annotations.Count == 0)
                    return false;
                capsule.Topology.Target.Clear();
                capsule.Topology.Annotations.Clear();
                return true;
            };

        private static Func<ContextCapsule, bool> TopologyResetStep(ContextCapsule capsule)
            => _ =>
            {
                if (capsule.Topology.Current == CapsuleTopologyReference.Empty
                    && capsule.Topology.Target.Count == 0
                    && capsule.Topology.Annotations.Count == 0)
                    return false;
                capsule.Topology = new CapsuleTopology(CapsuleTopologyReference.Empty, [], []);
                return true;
            };

        private static Func<ContextCapsule, bool> CompletenessDropDetailStep(ContextCapsule capsule)
            => _ =>
            {
                if (capsule.Completeness == null || capsule.Completeness.BindingIncompleteness.Count == 0)
                    return false;
                capsule.Completeness.BindingIncompleteness.Clear();
                return true;
            };

        private static Func<ContextCapsule, bool> CompletenessDropStep(ContextCapsule capsule)
            => _ =>
            {
                if (capsule.Completeness == null)
                    return false;
                capsule.Completeness = null;
                return true;
            };

        private static Func<ContextCapsule, bool> ClearStep<T>(List<T> items)
            => _ =>
            {
                if (items.Count == 0)
                    return false;
                items.Clear();
                return true;
            };

        private static Func<ContextCapsule, bool> ClearDictionaryStep(Dictionary<string, string> items)
            => _ =>
            {
                if (items.Count == 0)
                    return false;
                items.Clear();
                return true;
            };

        private static Func<ContextCapsule, bool> PathBoundStep(List<ImpactPath> paths)
            => _ =>
            {
                if (paths.Count == 0)
                    return false;
                var changed = false;
                foreach (var path in paths)
                {
                    if (path.Hops.Count > MaxBoundHopsPerPath)
                    {
                        path.Hops.RemoveRange(MaxBoundHopsPerPath, path.Hops.Count - MaxBoundHopsPerPath);
                        changed = true;
                    }
                }
                if (paths.Count > MaxBoundPaths)
                {
                    paths.RemoveRange(MaxBoundPaths, paths.Count - MaxBoundPaths);
                    changed = true;
                }
                return changed;
            };

        private static Func<ContextCapsule, bool> BoundSourceStep(List<CapsuleItem> items, int maxChars)
            => _ =>
            {
                if (items.Count == 0)
                    return false;
                var changed = false;
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item.Source == null || item.Source.Length <= maxChars)
                        continue;
                    items[i] = BoundItemSource(item, maxChars);
                    changed = true;
                }
                return changed;
            };

        private static CapsuleItem BoundItemSource(CapsuleItem item, int maxChars)
        {
            var source = item.Source!;
            var bounded = source.Substring(0, maxChars) + SourceTruncationMarker;
            return new CapsuleItem(
                item.SymbolId, item.Kind, item.FullyQualifiedName, item.Provenance, item.EdgeKind,
                bounded,
                item.DocumentPath != null
                    ? new DeclarationLocation(item.DocumentPath, item.StartLine ?? 0, item.StartColumn ?? 0, item.EndLine ?? 0, item.EndColumn ?? 0, IsGenerated: false)
                    : null,
                item.InclusionReason);
        }

        private sealed class SectionTrimmer
        {
            private readonly ContextCapsule _capsule;
            private readonly List<TrimmableSection> _sections;

            public SectionTrimmer(ContextCapsule capsule, IReadOnlyList<string> tierPriority)
            {
                _capsule = capsule;

                var sections = new List<TrimmableSection>();
                foreach (var name in tierPriority)
                {
                    var items = TierItems(capsule, name);
                    if (items != null)
                        sections.Add(TrimmableSection.Clear(name, items));
                }

                // Sections appended after the tiers, ordered so the greedy loop
                // (which trims from the end of this list first) drops the least
                // essential content first. Uncertainties are surfaced last among
                // the non-tier sections so semantic incompleteness is preserved as
                // long as possible.
                sections.AddRange(
                [
                    TrimmableSection.Clear("uncertainties", capsule.Uncertainties),
                    TrimmableSection.Clear("suggestedVerification", capsule.SuggestedVerification),
                    TrimmableSection.Clear("incomingPaths", capsule.IncomingPaths),
                    TrimmableSection.Clear("outgoingPaths", capsule.OutgoingPaths),
                    TrimmableSection.WithSteps("topology",
                        new TrimStep("summarized", TopologyDropTargetsStep(capsule)),
                        new TrimStep("budget_exhausted", TopologyResetStep(capsule))),
                    TrimmableSection.Clear("constraints", capsule.Constraints),
                    TrimmableSection.WithSteps("completeness",
                        new TrimStep("summarized", CompletenessDropDetailStep(capsule)),
                        new TrimStep("budget_exhausted", CompletenessDropStep(capsule))),
                    TrimmableSection.Clear("likelyChangeSites", capsule.LikelyChangeSites),
                    TrimmableSection.Clear("affectedPublicSurfaces", capsule.AffectedPublicSurfaces),
                    TrimmableSection.ClearDictionary("inclusionReasons", capsule.InclusionReasons),
                ]);

                _sections = sections;
            }

            public TruncationEntry? TrimNextLowestPriority()
            {
                for (var i = _sections.Count - 1; i >= 0; i--)
                {
                    var section = _sections[i];
                    while (section.Steps.Count > 0)
                    {
                        var step = section.Steps.Dequeue();
                        if (step.Apply(_capsule))
                            return new TruncationEntry(section.Name, step.Reason);
                    }
                    _sections.RemoveAt(i);
                }
                return null;
            }

            public TruncationEntry? BoundPathSection(string category)
            {
                foreach (var section in _sections)
                {
                    if (!string.Equals(section.Name, category, StringComparison.Ordinal))
                        continue;
                    var paths = PathItems(_capsule, category);
                    if (paths != null && PathBoundStep(paths)(_capsule))
                        return new TruncationEntry(section.Name, "summarized");
                    return null;
                }
                return null;
            }

            public TruncationEntry? BoundTierSources(string name, int maxChars)
            {
                foreach (var section in _sections)
                {
                    if (!string.Equals(section.Name, name, StringComparison.Ordinal))
                        continue;
                    if (BoundSourceStep(TierItems(_capsule, name)!, maxChars)(_capsule))
                        return new TruncationEntry(section.Name, "summarized");
                    return null;
                }
                return null;
            }

            private static List<ImpactPath>? PathItems(ContextCapsule capsule, string category)
                => category switch
                {
                    "incomingPaths" => capsule.IncomingPaths,
                    "outgoingPaths" => capsule.OutgoingPaths,
                    _ => null,
                };

            private static List<CapsuleItem>? TierItems(ContextCapsule capsule, string name)
                => name switch
                {
                    "contracts" => capsule.Contracts,
                    "directCallees" => capsule.DirectCallees,
                    "directCallers" => capsule.DirectCallers,
                    "registeredImplementations" => capsule.RegisteredImplementations,
                    "relevantTests" => capsule.RelevantTests,
                    "secondDegreeContext" => capsule.SecondDegreeContext,
                    "surroundingSource" => capsule.SurroundingSource,
                    _ => null,
                };
        }

        private sealed class TrimmableSection(string name, Queue<TrimStep> steps)
        {
            public string Name { get; } = name;
            public Queue<TrimStep> Steps { get; } = steps;

            public static TrimmableSection Clear<T>(string name, List<T> items)
                => new(name, new Queue<TrimStep>([new TrimStep("budget_exhausted", ClearStep(items))]));

            public static TrimmableSection ClearDictionary(string name, Dictionary<string, string> items)
                => new(name, new Queue<TrimStep>([new TrimStep("budget_exhausted", ClearDictionaryStep(items))]));

            public static TrimmableSection WithSteps(string name, params TrimStep[] steps)
                => new(name, new Queue<TrimStep>(steps));
        }

        private sealed record TrimStep(string Reason, Func<ContextCapsule, bool> Apply);
    }
}
