using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

namespace Lurp.Workspace;

internal static class DeclarationSpanComputer
{
    public static (DeclarationSpan Full, DeclarationSpan Signature, DeclarationSpan Body, DeclarationSpan Name)
        ComputeSpans(SyntaxNode node, string sourceText, Encoding encoding)
    {
        var fullCharSpan = node.FullSpan;
        var fullStart = CharOffsetToByteOffset(sourceText, fullCharSpan.Start, encoding);
        var fullEnd = CharOffsetToByteOffset(sourceText, fullCharSpan.End, encoding);
        var full = new DeclarationSpan(fullStart, fullEnd);

        var name = ComputeNameSpan(node, sourceText, encoding, full);
        var (body, signatureCharEnd) = ComputeBodyAndSignatureEnd(node, sourceText, encoding, fullCharSpan);
        var signature = new DeclarationSpan(fullStart, CharOffsetToByteOffset(sourceText, signatureCharEnd, encoding));

        return (full, signature, body, name);
    }

    private static DeclarationSpan ComputeNameSpan(SyntaxNode node, string sourceText, Encoding encoding, DeclarationSpan full)
    {
        static SyntaxToken? GetIdentifier(SyntaxNode n) => n switch
        {
            BaseTypeDeclarationSyntax t => t.Identifier,
            MethodDeclarationSyntax m => m.Identifier,
            ConstructorDeclarationSyntax c => c.Identifier,
            PropertyDeclarationSyntax p => p.Identifier,
            EventDeclarationSyntax e => e.Identifier,
            VariableDeclaratorSyntax v => v.Identifier,
            EnumMemberDeclarationSyntax em => em.Identifier,
            _ => null
        };

        var idToken = GetIdentifier(node);
        if (idToken != null)
        {
            var idStart = CharOffsetToByteOffset(sourceText, idToken.Value.SpanStart, encoding);
            var idEnd = CharOffsetToByteOffset(sourceText, idToken.Value.Span.End, encoding);
            return new DeclarationSpan(idStart, idEnd);
        }

        var tokens = node.ChildTokens().Where(t => t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken) || t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GlobalKeyword)).ToArray();
        if (tokens.Length > 0)
        {
            var firstId = tokens[0];
            return new DeclarationSpan(CharOffsetToByteOffset(sourceText, firstId.SpanStart, encoding),
                CharOffsetToByteOffset(sourceText, firstId.Span.End, encoding));
        }

        return full;
    }

    private static (DeclarationSpan Body, int SignatureCharEnd) ComputeBodyAndSignatureEnd(SyntaxNode node, string sourceText, Encoding encoding, Microsoft.CodeAnalysis.Text.TextSpan fullCharSpan)
    {
        if (node is MethodDeclarationSyntax method && method.Body != null)
            return (SpanFromCharSpan(sourceText, method.Body.Span, encoding), method.Body.SpanStart);

        if (node is MethodDeclarationSyntax methodExpr && methodExpr.ExpressionBody != null)
            return (SpanFromCharSpan(sourceText, methodExpr.ExpressionBody.Span, encoding), methodExpr.ExpressionBody.SpanStart);

        if (node is MethodDeclarationSyntax { Body: null, ExpressionBody: null })
            return (new DeclarationSpan(null, null), fullCharSpan.End);

        if (node is PropertyDeclarationSyntax { AccessorList: not null })
            return (new DeclarationSpan(null, null), fullCharSpan.End);

        if (node is PropertyDeclarationSyntax propExpr && propExpr.ExpressionBody != null)
            return (SpanFromCharSpan(sourceText, propExpr.ExpressionBody.Span, encoding), propExpr.ExpressionBody.SpanStart);

        if (node is BaseTypeDeclarationSyntax typeDecl)
        {
            if (typeDecl.OpenBraceToken.IsMissing || typeDecl.OpenBraceToken.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.None))
                return (new DeclarationSpan(null, null), fullCharSpan.End);
            var body = new DeclarationSpan(CharOffsetToByteOffset(sourceText, typeDecl.OpenBraceToken.SpanStart, encoding),
                CharOffsetToByteOffset(sourceText, typeDecl.CloseBraceToken.Span.End, encoding));
            return (body, typeDecl.OpenBraceToken.SpanStart);
        }

        if (node is EnumDeclarationSyntax enumDecl)
        {
            if (enumDecl.OpenBraceToken.IsMissing || enumDecl.OpenBraceToken.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.None))
                return (new DeclarationSpan(null, null), fullCharSpan.End);
            var body = new DeclarationSpan(CharOffsetToByteOffset(sourceText, enumDecl.OpenBraceToken.SpanStart, encoding),
                CharOffsetToByteOffset(sourceText, enumDecl.CloseBraceToken.Span.End, encoding));
            return (body, enumDecl.OpenBraceToken.SpanStart);
        }

        if (node is NamespaceDeclarationSyntax nsDecl)
        {
            if (nsDecl.OpenBraceToken.IsMissing || nsDecl.OpenBraceToken.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.None))
                return (new DeclarationSpan(null, null), fullCharSpan.End);
            var body = new DeclarationSpan(CharOffsetToByteOffset(sourceText, nsDecl.OpenBraceToken.SpanStart, encoding),
                CharOffsetToByteOffset(sourceText, nsDecl.CloseBraceToken.Span.End, encoding));
            return (body, nsDecl.OpenBraceToken.SpanStart);
        }

        return (new DeclarationSpan(null, null), fullCharSpan.End);
    }

    private static DeclarationSpan SpanFromCharSpan(string sourceText, Microsoft.CodeAnalysis.Text.TextSpan charSpan, Encoding encoding)
    {
        return new DeclarationSpan(CharOffsetToByteOffset(sourceText, charSpan.Start, encoding),
            CharOffsetToByteOffset(sourceText, charSpan.End, encoding));
    }

    public static int CharOffsetToByteOffset(string text, int charOffset, Encoding encoding)
    {
        if (charOffset <= 0)
            return 0;
        if (charOffset >= text.Length)
            return encoding.GetByteCount(text);

        return encoding.GetByteCount(text.AsSpan(0, charOffset));
    }

    public static Encoding GetEncoding(string encodingName)
    {
        return encodingName?.ToLowerInvariant() switch
        {
            "utf-8" => Encoding.UTF8,
            "utf-8-bom" => Encoding.UTF8,
            "utf-16-le" => Encoding.Unicode,
            "utf-16-be" => Encoding.BigEndianUnicode,
            _ => Encoding.UTF8,
        };
    }
}