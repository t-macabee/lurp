namespace Lurp.Storage;

public sealed class TextSearchResult
{
    public TextSearchResult(string documentPath, int startLine, int startColumn, int endLine, int endColumn, string lineText, int startOffset)
    {
        DocumentPath = documentPath ?? throw new ArgumentNullException(nameof(documentPath));
        LineText = lineText ?? throw new ArgumentNullException(nameof(lineText));
        StartLine = startLine;
        StartColumn = startColumn;
        EndLine = endLine;
        EndColumn = endColumn;
        StartOffset = startOffset;
    }

    public string DocumentPath { get; }
    /// <summary>1-based line number.</summary>
    public int StartLine { get; }
    /// <summary>0-based column (character offset within the line).</summary>
    public int StartColumn { get; }
    /// <summary>1-based line number of the end position (exclusive).</summary>
    public int EndLine { get; }
    /// <summary>0-based column of the end position.</summary>
    public int EndColumn { get; }
    public string LineText { get; }
    /// <summary>Character offset of the match start within the decoded document content; used for cursor pagination.</summary>
    public int StartOffset { get; }
}
