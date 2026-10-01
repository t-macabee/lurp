using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class StringLiteralReflectionExtractor(ReflectionExtractionContext context)
{
    internal List<EdgeRecord> Extract(SyntaxNode root, SemanticModel semanticModel)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
                continue;

            var text = literal.Token.ValueText;
            if (string.IsNullOrEmpty(text) || text.Length < 3)
                continue;

            if (IsNoiseString(text))
                continue;

            if (!context.TypesByName.TryGetValue(text, out var matchedSymbols) &&
                !context.MembersByName.TryGetValue(text, out matchedSymbols))
            {
                continue;
            }

            if (matchedSymbols == null || matchedSymbols.Count == 0)
                continue;

            var sourceId = context.GetContainingMemberSymbolId(literal, semanticModel);
            if (sourceId == null)
                continue;

            var targetIds = new List<string>(matchedSymbols.Count);
            foreach (var symbol in matchedSymbols)
            {
                var targetId = context.MakeSymbolId(symbol);
                if (targetId != null)
                    targetIds.Add(targetId);
            }

            if (targetIds.Count == 0)
                continue;

            targetIds.Sort(StringComparer.Ordinal);

            var loc = context.GetLocationInfo(literal.GetLocation());

            foreach (var targetId in targetIds)
            {
                if (sourceId == targetId)
                    continue;

                var key = (sourceId, targetId, nameof(EdgeKind.ReflectionNameCandidate));
                if (!seen.Add(key))
                    continue;

                edges.Add(new EdgeRecord
                {
                    SourceSymbolId = sourceId,
                    TargetSymbolId = targetId,
                    Kind = nameof(EdgeKind.ReflectionNameCandidate),
                    Provenance = Provenance.NameCandidate,
                    SnapshotId = context.SnapshotId,
                    ExtractorVersion = ExtractorConstants.ReflectionExtractor,
                    SourceDocumentPath = loc.path,
                    SourceStartLine = loc.startLine,
                    SourceStartColumn = loc.startColumn,
                    SourceEndLine = loc.endLine,
                    SourceEndColumn = loc.endColumn,
                    IsCrossGenerated = context.IsGenerated(loc.path)
                });
            }
        }

        return edges;
    }

    private static bool IsNoiseString(string text)
    {
        if (text.All(char.IsDigit))
            return true;
        if (text.Contains(' ') && !text.Contains('.') && !IsPascalCase(text) && !IsCamelCase(text))
            return true;
        return false;
    }

    private static bool IsPascalCase(string text)
    {
        return text.Length > 0 && char.IsUpper(text[0]) && text.Any(char.IsLower);
    }

    private static bool IsCamelCase(string text)
    {
        return text.Length > 0 && char.IsLower(text[0]) && text.Any(char.IsUpper);
    }
}