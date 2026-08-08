using Lurp.Storage;
using Lurp.Shared;
using Lurp.Workspace;

namespace Lurp.Adapters;

public sealed record ExtractionContext(
    string AssemblyIdentity,
    string SnapshotId,
    string ExtractorVersion,
    List<EdgeRecord> Edges,
    HashSet<(string Source, string Target, string Kind)> Seen,
    EdgeLocationResolver LocationResolver,
    BindingIncompletenessCollector? Incompleteness = null,
    List<AnnotationRecord>? Annotations = null
);
