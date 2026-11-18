// Purpose: JSON converters for snapshot-manifest identity types.
// Owns: serialization of SnapshotId/WorkspaceId/document-version maps.
// Must not contain: manifest construction or freshness logic.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lurp.Workspace;

public sealed partial class SnapshotManifest
{
    private const string OneWayExportMessage = "Snapshot manifest JSON is a one-way export and cannot be read back.";

    private sealed class SnapshotIdConverter : JsonConverter<SnapshotId>
    {
        public override SnapshotId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException(OneWayExportMessage);

        public override void Write(Utf8JsonWriter writer, SnapshotId value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }
    }

    private sealed class NullableSnapshotIdConverter : JsonConverter<SnapshotId?>
    {
        public override SnapshotId? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException(OneWayExportMessage);

        public override void Write(Utf8JsonWriter writer, SnapshotId? value, JsonSerializerOptions options)
        {
            if (value is null) writer.WriteNullValue();
            else writer.WriteStringValue(value.Value.ToString());
        }
    }

    private sealed class WorkspaceIdConverter : JsonConverter<WorkspaceId>
    {
        public override WorkspaceId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException(OneWayExportMessage);

        public override void Write(Utf8JsonWriter writer, WorkspaceId value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("git_root", value.GitRoot);
            writer.WriteString("solution_path", value.SolutionPath);
            writer.WriteString("value", value.Value);
            writer.WriteEndObject();
        }
    }

    private sealed class DocumentVersionMapConverter
        : JsonConverter<Dictionary<DocumentId, DocumentVersionId>>
    {
        public override Dictionary<DocumentId, DocumentVersionId> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException(OneWayExportMessage);

        public override void Write(Utf8JsonWriter writer, Dictionary<DocumentId, DocumentVersionId> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var kvp in value) writer.WriteString(kvp.Key.ToString(), kvp.Value.ToString());
            writer.WriteEndObject();
        }
    }
}
