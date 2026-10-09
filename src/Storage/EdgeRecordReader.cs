using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal static class EdgeRecordReader
{
    internal static EdgeRecord Read(SqliteDataReader reader, int offset)
    {
        return new EdgeRecord
        {
            SourceSymbolId = reader.GetString(offset),
            TargetSymbolId = reader.GetString(offset + 1),
            Kind = reader.GetString(offset + 2),
            Provenance = reader.IsDBNull(offset + 3) ? string.Empty : reader.GetString(offset + 3),
            SnapshotId = reader.IsDBNull(offset + 4) ? string.Empty : reader.GetString(offset + 4),
            ExtractorVersion = reader.IsDBNull(offset + 5) ? string.Empty : reader.GetString(offset + 5),
            SourceDocumentPath = reader.IsDBNull(offset + 6) ? null : reader.GetString(offset + 6),
            SourceStartLine = reader.IsDBNull(offset + 7) ? null : reader.GetInt32(offset + 7),
            SourceStartColumn = reader.IsDBNull(offset + 8) ? null : reader.GetInt32(offset + 8),
            SourceEndLine = reader.IsDBNull(offset + 9) ? null : reader.GetInt32(offset + 9),
            SourceEndColumn = reader.IsDBNull(offset + 10) ? null : reader.GetInt32(offset + 10),
            IsCrossGenerated = reader.GetBoolean(offset + 11),
            TypeArgumentsJson = reader.IsDBNull(offset + 12) ? null : reader.GetString(offset + 12),
            ReceiverTypeConstraintsJson = reader.IsDBNull(offset + 13) ? null : reader.GetString(offset + 13)
        };
    }
}
