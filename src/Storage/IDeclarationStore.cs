using System.Collections.Generic;

namespace Lurp.Storage
{
    public interface IDeclarationStore
    {
        void SaveDeclarations(string snapshotId, IEnumerable<SymbolDeclaration> declarations);
        SymbolInfo? GetSymbolInfo(string symbolId, string snapshotId);
        string? GetSymbolSource(string symbolId, string snapshotId, ViewKind viewKind, bool includeGenerated = false);
        string? GetContainingTypeSource(string symbolId, string snapshotId);
        string? GetSurroundingLines(string symbolId, string snapshotId, int contextLines);

        void DeleteDeclarationsByDocumentVersionIds(IEnumerable<string> documentVersionIds);
        List<string> GetSymbolIdsByDocumentVersionIds(string snapshotId, IEnumerable<string> documentVersionIds);
        string? ResolveSymbolByLocation(string relativePath, int line, string snapshotId, bool includeGenerated = false);
    }
}
