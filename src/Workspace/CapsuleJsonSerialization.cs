// Purpose: serialize a ContextCapsule to its persisted JSON shape.
// Owns: the capsule JSON contract only.
// Must not contain: data model definitions or assembly/budget logic.

using System.Text.Json.Serialization;

namespace Lurp.Workspace
{
    // Canonical serializer for the emitted capsule representation; the handler
    // writes exactly this. Note which field describes it: estimatedTokens is a
    // content estimate and is smaller than this serialization, because per-item
    // identity/provenance framing is uncounted navigation metadata.
    // estimatedArtifactTokens is the estimate of this serialization itself.
    internal static class ContextCapsuleJson
    {
        internal static readonly System.Text.Json.JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // Same field contract, one line per document : used by --output=jsonl on the
        // single-tier continuation, never for the capsule itself.
        internal static readonly System.Text.Json.JsonSerializerOptions CompactOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        internal static string Serialize(ContextCapsule capsule)
            => System.Text.Json.JsonSerializer.Serialize(capsule, Options);
    }
}
