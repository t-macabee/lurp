namespace Lurp.Storage;

public interface IDeclarationStore
{
    void SaveDeclarations(string snapshotId, IEnumerable<SymbolDeclaration> declarations);
    IndexedSymbolInfo? GetSymbolInfo(string symbolId, string snapshotId);
    string? GetSymbolSource(string symbolId, string snapshotId, ViewKind viewKind, bool includeGenerated = false);
    string? GetContainingTypeSource(string symbolId, string snapshotId, bool includeGenerated);
    string? GetSurroundingLines(string symbolId, string snapshotId, int contextLines, bool includeGenerated);
    List<DeclarationLocation> GetDeclarationLocations(string symbolId, string snapshotId, bool includeGenerated = false);
    Dictionary<string, List<string>> GetDocumentVersionIdsBySymbol(string snapshotId);
    Dictionary<string, List<string>> GetDocumentVersionIdsBySymbol(string snapshotId, IEnumerable<string> symbolIds);
    Dictionary<string, SymbolDiffInfo> GetSymbolDiffInfoBySymbol(string snapshotId);

    void CopySnapshotDeclarations(string fromSnapshotId, string toSnapshotId);
    void DeleteSnapshotDeclarationsByDocumentVersionIds(string snapshotId, IEnumerable<string> documentVersionIds);
    List<string> GetSymbolIdsByDocumentVersionIds(string snapshotId, IEnumerable<string> documentVersionIds);
    NavigationTarget? NavigateToLocation(string relativePath, int line, string snapshotId, bool includeGenerated = false);
}