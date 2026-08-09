// Purpose: JSON converters for snapshot-manifest identity types.
// Owns: (de)serialization of SnapshotId/WorkspaceId/document-version maps.
// Must not contain: manifest construction or freshness logic.

using System.Text.Json;
using System.Text.Json.Serialization;
using Lurp.Storage;

namespace Lurp.Workspace;

public sealed partial class SnapshotManifest
{
    private sealed class SnapshotIdConverter : JsonConverter<SnapshotId>
    {
        public override SnapshotId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => SnapshotId.Parse(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, SnapshotId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }

    private sealed class NullableSnapshotIdConverter : JsonConverter<SnapshotId?>
    {
        public override SnapshotId? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            return SnapshotId.Parse(reader.GetString()!);
        }

        public override void Write(Utf8JsonWriter writer, SnapshotId? value, JsonSerializerOptions options)
        {
            if (value is null) writer.WriteNullValue();
            else writer.WriteStringValue(value.Value.ToString());
        }
    }

    private sealed class WorkspaceIdConverter : JsonConverter<WorkspaceId>
    {
        public override WorkspaceId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected JSON object for WorkspaceId.");

            string? gitRoot = null, solutionPath = null, value = null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var prop = reader.GetString();
                reader.Read();
                switch (prop)
                {
                    case "git_root" or "gitRoot": gitRoot = reader.GetString(); break;
                    case "solution_path" or "solutionPath": solutionPath = reader.GetString(); break;
                    case "value": value = reader.GetString(); break;
                    default: reader.Skip(); break;
                }
            }

            if (gitRoot != null && solutionPath != null)
                return WorkspaceId.Create(gitRoot, solutionPath);

            if (value != null)
                return ParseWorkspaceUri(value);

            throw new JsonException("Insufficient data to reconstruct WorkspaceId.");
        }

        public override void Write(Utf8JsonWriter writer, WorkspaceId value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("git_root", value.GitRoot);
            writer.WriteString("solution_path", value.SolutionPath);
            writer.WriteString("value", value.Value);
            writer.WriteEndObject();
        }

        private static WorkspaceId ParseWorkspaceUri(string uri)
        {

            const string prefix = "workspace://";
            if (!uri.StartsWith(prefix, StringComparison.Ordinal))
                throw new JsonException($"Invalid WorkspaceId URI: {uri}");

            var rest = uri[prefix.Length..];
            var slashIndex = rest.IndexOf('/');
            if (slashIndex < 0)
                throw new JsonException($"Invalid WorkspaceId URI (no root/solution split): {uri}");

            var gitRoot = rest[..slashIndex];
            var slnPath = rest[(slashIndex + 1)..];
            var fullSlnPath = Path.GetFullPath(Path.Combine(gitRoot, slnPath));
            return WorkspaceId.Create(gitRoot, fullSlnPath);
        }
    }

    private sealed class DocumentVersionMapConverter
        : JsonConverter<Dictionary<DocumentId, DocumentVersionId>>
    {
        public override Dictionary<DocumentId, DocumentVersionId> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var result = new Dictionary<DocumentId, DocumentVersionId>();

            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected JSON object for documentVersions.");

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var docPath = reader.GetString()!;
                reader.Read();
                var hash = reader.GetString()!;
                result[new DocumentId(docPath)] = new DocumentVersionId(hash);
            }

            return result;
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<DocumentId, DocumentVersionId> value,JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var kvp in value)
            {
                writer.WriteString(kvp.Key.ToString(), kvp.Value.ToString());
            }
            writer.WriteEndObject();
        }
    }
}
