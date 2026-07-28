using Lurp.Storage;
using Lurp.Shared;

namespace Lurp.Adapters;

public sealed record ExtractionContext(
    string AssemblyIdentity,
    string SnapshotId,
    List<EdgeRecord> Edges,
    HashSet<(string Source, string Target, string Kind)> Seen,
    EdgeLocationResolver LocationResolver
);
