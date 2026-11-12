using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

internal sealed class MemberEdgeExtractionContext(ProjectExtractionInputs inputs) : ExtractionContextBase(inputs)
{
    internal (string? path, int? startLine, int? startColumn, int? endLine, int? endColumn)?
        GetMemberSourceLocation(ISymbol member)
    {
        var result = LocationResolver.Resolve(member);
        if (result.path == null && result.sl == null)
            return null;
        return result;
    }
}

internal interface IMemberEdgeExtractor
{
    List<EdgeRecord> Extract();
}