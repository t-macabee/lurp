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
    // recorded in omittedTiers and truncatedCategories. surroundingSource is
    // low-value bulk (sibling declarations that heavily overlap the anchor) and
    // is cleared before high-signal small sections such as inclusionReasons and
    // affectedPublicSurfaces. The anchor is never dropped; as a last resort its
    // source is bounded to fit the remaining budget (a "summarized" entry), so
    // --budget always bounds the content basis it is documented to bound. Only
    // when the residual non-anchor content alone still exceeds the budget is the
    // overflow declared with budget_exhausted. estimatedTokens is set to the
    // settled CONTENT measure of the capsule : the serialized artifact is
    // larger, because per-item identity and provenance framing is uncounted
    // navigation metadata; the whole-artifact figure is reported separately as
    // estimatedArtifactTokens.
    //
    // omittedTiers carries exactly ONE terminal record per category: a later trim
    // of a category supersedes its earlier record in place, so the list describes
    // the settled capsule rather than the history of how it settled.
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
            var estimate = EnforceContentBudget(capsule, budget, tierPriority);
            StampArtifactEstimate(capsule);
            return estimate;
        }

        private static int EnforceContentBudget(ContextCapsule capsule, int budget, IReadOnlyList<string> tierPriority)
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
                    // dropped; bound its source to fit the remaining budget so
                    // the delivered estimate honors the basis --budget is
                    // documented to bound. Only when the residual non-anchor
                    // content alone still exceeds the budget (a pathologically
                    // small budget) is the overflow declared, honestly, with
                    // budget_exhausted.
                    if (BoundAnchorSourceToFit(capsule, budget))
                        RecordTruncation(capsule, new TruncationEntry("anchor", "summarized"));
                    capsule.EstimatedTokens = Measure(capsule);
                    if (capsule.EstimatedTokens > budget)
                        RecordTruncation(capsule, new TruncationEntry("anchor", "budget_exhausted"));
                    return capsule.EstimatedTokens;
                }
                RecordTruncation(capsule, entry);
            }
        }

        /// <summary>
        /// Records the whole-serialization estimate the consumer needs to size a
        /// context window. It is deliberately NOT the budget basis: budgeting on
        /// the whole serialization was measured to gut tier content at realistic
        /// budgets. The field is part of the document it measures, so its own
        /// digits change the length; iterate to a fixed point, and accept the
        /// last value if the length oscillates across a digit boundary (the
        /// residual error is one character, far below one token).
        /// </summary>
        private static void StampArtifactEstimate(ContextCapsule capsule)
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var estimate = ContextCapsuleJson.Serialize(capsule).Length / 4;
                if (estimate == capsule.EstimatedArtifactTokens)
                    return;
                capsule.EstimatedArtifactTokens = estimate;
            }
        }

        /// <summary>
        /// Content measure of the capsule: the source text of the anchor and
        /// every item, plus the serialized weight of the substantive non-source
        /// sections. Per-item identity framing is not counted.
        /// </summary>
        internal static int Measure(ContextCapsule capsule)
            => MeasureChars(capsule) / 4;

        private static int MeasureChars(ContextCapsule capsule)
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
            return chars;
        }

        /// <summary>
        /// Last-resort trim: bounds the anchor's own source so the settled
        /// content measure fits the budget, keeping the truncation marker
        /// whenever it fits so the bounded source declares its own truncation
        /// the same way tier item sources do. Returns false when the anchor
        /// already fits and the overage is residual non-anchor content.
        /// </summary>
        private static bool BoundAnchorSourceToFit(ContextCapsule capsule, int budget)
        {
            var source = capsule.Anchor.Source;
            var otherChars = MeasureChars(capsule) - source.Length;
            var allowed = Math.Max(budget * 4 - otherChars, 0);
            if (source.Length <= allowed)
                return false;

            var keepsMarker = allowed >= SourceTruncationMarker.Length;
            var contentChars = keepsMarker ? allowed - SourceTruncationMarker.Length : allowed;
            capsule.Anchor.Source = source.Substring(0, contentChars)
                + (keepsMarker ? SourceTruncationMarker : string.Empty);
            return true;
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

            // One terminal record per category. A category can be trimmed more
            // than once (bounded, then cleared), and a chronological log of those
            // steps forces the consumer to reconstruct which record is current.
            // The later record supersedes the earlier one in place, so ordering
            // stays deterministic and the list always describes the emitted state.
            var existing = capsule.OmittedTiers.FindIndex(
                record => string.Equals(record.Category, entry.Category, StringComparison.Ordinal));
            if (existing >= 0)
                capsule.OmittedTiers[existing] = entry;
            else
                capsule.OmittedTiers.Add(entry);
        }

        private static Func<ContextCapsule, bool> TopologyDropTargetsStep(ContextCapsule capsule)
            => _ =>
            {
                if (capsule.Topology == null
                    || (capsule.Topology.Target.Count == 0 && capsule.Topology.Annotations.Count == 0))
                    return false;
                capsule.Topology.Target.Clear();
                capsule.Topology.Annotations.Clear();
                return true;
            };

        // Dropped, not zeroed. A retained topology whose counts are all zero
        // reads as "no incoming or outgoing references" : a positive claim, and a
        // false one beside a populated directCallers tier. Absence plus the
        // omittedTiers record says what is true: the section was not emitted.
        private static Func<ContextCapsule, bool> TopologyResetStep(ContextCapsule capsule)
            => _ =>
            {
                if (capsule.Topology == null)
                    return false;
                capsule.Topology = null;
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

        // The omittedTiers.* entries are how a consumer interprets and recovers
        // the omissions this trim pass creates, so they must outlive the pressure
        // that makes them necessary : clearing them first (they are the
        // lowest-priority section) left the capsule that omitted the most as the
        // one with no instructions for recovering anything. They cost ~50 tokens.
        private static Func<ContextCapsule, bool> ClearDictionaryStep(Dictionary<string, string> items)
            => _ =>
            {
                var removable = items.Keys
                    .Where(static key => !key.StartsWith("omittedTiers.", StringComparison.Ordinal))
                    .ToList();
                if (removable.Count == 0)
                    return false;
                foreach (var key in removable)
                    items.Remove(key);
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
                item.InclusionReason,
                item.Relationship,
                item.Direct);
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

                // surroundingSource is low-value bulk: sibling declarations that
                // heavily overlap the anchor. It must be cleared before the
                // small high-signal sections (inclusionReasons,
                // affectedPublicSurfaces), so it moves to the end of the list
                // (the greedy loop trims from the end first) regardless of its
                // position in the tier priority. Source bounding is unaffected:
                // BoundTierSources looks sections up by name.
                var surrounding = sections.FirstOrDefault(
                    static section => section.Name == "surroundingSource");
                if (surrounding != null)
                {
                    sections.Remove(surrounding);
                    sections.Add(surrounding);
                }
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
