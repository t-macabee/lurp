const fs = require('fs');
const { execSync } = require('child_process');
const path = require('path');

const srcRoot = 'C:\\Users\\Tarik\\Desktop\\lurp';

function readFile(p) { return fs.readFileSync(p, 'utf8'); }
function writeFile(p, c) { fs.writeFileSync(p, c, 'utf8'); }
function gitCheckout(rel) {
  try { execSync(`git checkout -- "${rel}"`, { cwd: srcRoot, stdio: 'pipe' }); } catch {}
}

// ============================================================
// 1. CA1510: if (x == null) throw new ArgumentNullException(nameof(x)); → ArgumentNullException.ThrowIfNull(x);
// ============================================================
const ca1510Files = [
  { file: 'src/Workspace/PolymorphismExtractor.cs', old: '        if (compilation == null) throw new ArgumentNullException(nameof(compilation));\n        if (snapshotId == null) throw new ArgumentNullException(nameof(snapshotId));', 
    new: '        ArgumentNullException.ThrowIfNull(compilation);\n        ArgumentNullException.ThrowIfNull(snapshotId);' },
  { file: 'src/Workspace/ReflectionExtractor.cs', old: '        if (compilation == null) throw new ArgumentNullException(nameof(compilation));\n        if (snapshotId == null) throw new ArgumentNullException(nameof(snapshotId));',
    new: '        ArgumentNullException.ThrowIfNull(compilation);\n        ArgumentNullException.ThrowIfNull(snapshotId);' },
  { file: 'src/Workspace/SymbolExtractor.cs', old: '        if (compilation == null) throw new ArgumentNullException(nameof(compilation));\n        if (documentContents == null) throw new ArgumentNullException(nameof(documentContents));\n        if (documentVersions == null) throw new ArgumentNullException(nameof(documentVersions));\n        if (generatedDocuments == null) throw new ArgumentNullException(nameof(generatedDocuments));\n        if (snapshotId == null) throw new ArgumentNullException(nameof(snapshotId));',
    new: '        ArgumentNullException.ThrowIfNull(compilation);\n        ArgumentNullException.ThrowIfNull(documentContents);\n        ArgumentNullException.ThrowIfNull(documentVersions);\n        ArgumentNullException.ThrowIfNull(generatedDocuments);\n        ArgumentNullException.ThrowIfNull(snapshotId);' },
  { file: 'src/Workspace/SnapshotManifest.cs', old: '        if (snapshotStore == null)\n            throw new ArgumentNullException(nameof(snapshotStore));',
    new: '        ArgumentNullException.ThrowIfNull(snapshotStore);' },
];
let count = 0;
for (const fix of ca1510Files) {
  const fp = path.join(srcRoot, fix.file);
  let c = readFile(fp);
  if (c.includes(fix.old)) { c = c.replace(fix.old, fix.new); writeFile(fp, c); count++; }
}
console.log(`CA1510: ${count} files fixed`);

// ============================================================
// 2. ConvertIfStatementToReturnStatement (2 clear cases)
// ============================================================
{
  const fp = path.join(srcRoot, 'src/Workspace/StringLiteralReflectionExtractor.cs');
  let c = readFile(fp);
  const old = `    private static bool IsNoiseString(string text)\n    {\n        if (text.All(char.IsDigit))\n            return true;\n        if (text.Contains(' ') && !text.Contains('.') && !IsPascalCase(text) && !IsCamelCase(text))\n            return true;\n        return false;\n    }`;
  const nw = `    private static bool IsNoiseString(string text)\n    {\n        return text.All(char.IsDigit)\n            || (text.Contains(' ') && !text.Contains('.') && !IsPascalCase(text) && !IsCamelCase(text));\n    }`;
  if (c.includes(old)) { c = c.replace(old, nw); writeFile(fp, c); console.log('Fixed StringLiteralReflectionExtractor'); }
}
{
  const fp = path.join(srcRoot, 'src/Workspace/WorkspaceInfo.cs');
  let c = readFile(fp);
  const old = `    private static bool IsGeneratedDocument(byte[] bytes, string relPath)\n    {\n        if (IsGeneratedPath(relPath))\n            return true;\n\n        if (IsGeneratedHeader(bytes))\n            return true;\n\n        return false;\n    }`;
  const nw = `    private static bool IsGeneratedDocument(byte[] bytes, string relPath)\n    {\n        return IsGeneratedPath(relPath) || IsGeneratedHeader(bytes);\n    }`;
  if (c.includes(old)) { c = c.replace(old, nw); writeFile(fp, c); console.log('Fixed WorkspaceInfo'); }
}

// ============================================================
// 3. ConvertToStaticClass (Program.cs)
// ============================================================
{
  const fp = path.join(srcRoot, 'src/Program.cs');
  let c = readFile(fp);
  c = c.replace('public class Program', 'public static class Program');
  writeFile(fp, c);
  console.log('Fixed ConvertToStaticClass');
}

// ============================================================
// 4. MergeConditionalExpression (SearchSymbolStore.cs)
// ============================================================
{
  const fp = path.join(srcRoot, 'src/Storage/SearchSymbolStore.cs');
  let c = readFile(fp);
  c = c.replace('ranks != null ? ranks[^1] : null,', 'ranks?[^1],');
  writeFile(fp, c);
  console.log('Fixed MergeConditionalExpression');
}

// ============================================================
// 5. MergeIntoLogicalPattern (DeclarationSpanComputer.cs)
// ============================================================
{
  const fp = path.join(srcRoot, 'src/Workspace/DeclarationSpanComputer.cs');
  let c = readFile(fp);
  c = c.replace(
    '        if (node is MethodDeclarationSyntax { Body: null, ExpressionBody: null }\n            || node is PropertyDeclarationSyntax { AccessorList: not null })',
    '        if (node is MethodDeclarationSyntax { Body: null, ExpressionBody: null } or PropertyDeclarationSyntax { AccessorList: not null })'
  );
  writeFile(fp, c);
  console.log('Fixed MergeIntoLogicalPattern');
}

// ============================================================
// 6. RedundantAlwaysMatchSubpattern (ThrowsEdgeExtractor.cs)
// ============================================================
{
  const fp = path.join(srcRoot, 'src/Workspace/ThrowsEdgeExtractor.cs');
  let c = readFile(fp);
  c = c.replace('else if (node is ThrowExpressionSyntax { Expression: not null } throwExpression)', 'else if (node is ThrowExpressionSyntax throwExpression)');
  writeFile(fp, c);
  console.log('Fixed RedundantAlwaysMatchSubpattern');
}

// ============================================================
// 7. RedundantTypeDeclarationBody (GoldenAdapterTests.cs)
// ============================================================
{
  const fp = path.join(srcRoot, 'tests/GoldenAdapterTests.cs');
  let c = readFile(fp);
  c = c.replace('public sealed class SerializationAdapterExtractorTest : InMemoryTestBase\n{\n}', 'public sealed class SerializationAdapterExtractorTest : InMemoryTestBase;');
  writeFile(fp, c);
  console.log('Fixed RedundantTypeDeclarationBody');
}

// ============================================================
// 8. ReplaceWithFieldKeyword (ContextTierContext.cs)
// ============================================================
{
  const fp = path.join(srcRoot, 'src/Workspace/ContextTierContext.cs');
  let c = readFile(fp);
  c = c.replace('    private readonly Dictionary<(string CandidateTypeId, string ReceiverTypeId), bool> _assignabilityCache = [];\n    private readonly Dictionary<(string SymbolId, bool IncludeGenerated), DeclarationLocation?> _locationCache = [];\n    private IReadOnlyList<string>? _effectiveSymbolIds;\n    private bool? _hasUnmodeledRegistrations;',
    '    private readonly Dictionary<(string CandidateTypeId, string ReceiverTypeId), bool> _assignabilityCache = [];\n    private readonly Dictionary<(string SymbolId, bool IncludeGenerated), DeclarationLocation?> _locationCache = [];\n    private bool? _hasUnmodeledRegistrations;');
  c = c.replace('internal IReadOnlyList<string> EffectiveSymbolIds => _effectiveSymbolIds ??= ComputeEffectiveSymbolIds();',
    'internal IReadOnlyList<string> EffectiveSymbolIds => field ??= ComputeEffectiveSymbolIds();');
  writeFile(fp, c);
  console.log('Fixed ReplaceWithFieldKeyword');
}

// ============================================================
// 9. UseUtf8StringLiteral (DeclarationReadStore.cs)
// ============================================================
{
  const fp = path.join(srcRoot, 'src/Storage/DeclarationReadStore.cs');
  let c = readFile(fp);
  c = c.replace('Encoding.UTF8.GetBytes("<DECLARED_NAME>")', '"<DECLARED_NAME>"u8.ToArray()');
  writeFile(fp, c);
  console.log('Fixed UseUtf8StringLiteral');
}

// ============================================================
// 10. CA1859 (TimingsHandler.cs)
// ============================================================
{
  const fp = path.join(srcRoot, 'src/Handlers/TimingsHandler.cs');
  let c = readFile(fp);
  c = c.replace('private static void ShowTimingsForSnapshot(IIndexStore store, string snapshotId, bool asJson)',
    'private static void ShowTimingsForSnapshot(SqliteIndexStore store, string snapshotId, bool asJson)');
  writeFile(fp, c);
  console.log('Fixed CA1859');
}

// ============================================================
// 11. RedundantVerbatimStringPrefix (EdgeOperationsStore.cs)
// ============================================================
{
  const fp = path.join(srcRoot, 'src/Storage/EdgeOperationsStore.cs');
  let c = readFile(fp);
  c = c.replace(`identity.Replace(@"\", @"\\").Replace(@"%", @"\%").Replace(@"_", @"\_")`,
    `identity.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")`);
  writeFile(fp, c);
  console.log('Fixed RedundantVerbatimStringPrefix');
}

// ============================================================
// 12. SimplifyStringInterpolation (HelpText.cs)
// ============================================================
{
  const fp = path.join(srcRoot, 'src/HelpText.cs');
  let c = readFile(fp);
  c = c.replace('$"  --mode={entry.Name.PadRight(20)}{entry.HelpText}"', '$"  --mode={entry.Name,-20}{entry.HelpText}"');
  c = c.replace('$"    {flag.PadRight(FlagColumn)}{lines[0]}"', '$"    {flag,-FlagColumn}{lines[0]}"');
  writeFile(fp, c);
  console.log('Fixed SimplifyStringInterpolation');
}

// ============================================================
// 13. CanSimplifyDictionaryTryGetValueWithGetValueOrDefault
// ============================================================
{
  const fp = path.join(srcRoot, 'src/Workspace/SnapshotManifest.cs');
  let c = readFile(fp);
  c = c.replace(
    'CompilationOptionsFingerprint = CompilationOptionsFingerprints.TryGetValue(kvp.Key, out var fp)\n                ? fp\n                : null',
    'CompilationOptionsFingerprint = CompilationOptionsFingerprints.GetValueOrDefault(kvp.Key)');
  writeFile(fp, c);
}
{
  const fp = path.join(srcRoot, 'src/Workspace/CrossDocumentEdgeRefresher.cs');
  let c = readFile(fp);
  c = c.replace('projectDocCount.TryGetValue(p, out var c) ? c : 0', 'projectDocCount.GetValueOrDefault(p)');
  writeFile(fp, c);
}
console.log('Fixed CanSimplifyDictionaryTryGetValueWithGetValueOrDefault');

// ============================================================
// 14. JoinNullCheckWithUsage
// ============================================================
{
  const fp = path.join(srcRoot, 'src/Workspace/CrossDocumentEdgeRefresher.cs');
  let c = readFile(fp);
  c = c.replace(
    '            var compilation = await project.GetCompilationAsync(cancellationToken);\n            if (compilation == null)\n                throw new InvalidOperationException($"Compilation loader: GetCompilationAsync returned null for project \'{project.Name}\' during cross-document edge refresh.");',
    '            var compilation = await project.GetCompilationAsync(cancellationToken)\n                ?? throw new InvalidOperationException($"Compilation loader: GetCompilationAsync returned null for project \'{project.Name}\' during cross-document edge refresh.");');
  writeFile(fp, c);
}
{
  const fp = path.join(srcRoot, 'src/Workspace/IncrementalIndexer.cs');
  let c = readFile(fp);
  c = c.replace(
    '            var compilation = await project.GetCompilationAsync(cancellationToken);\n            if (compilation == null)\n                throw new InvalidOperationException($"Compilation loader: GetCompilationAsync returned null for project \'{project.Name}\' during incremental extraction.");',
    '            var compilation = await project.GetCompilationAsync(cancellationToken)\n                ?? throw new InvalidOperationException($"Compilation loader: GetCompilationAsync returned null for project \'{project.Name}\' during incremental extraction.");');
  writeFile(fp, c);
}
console.log('Fixed JoinNullCheckWithUsage');

// ============================================================
// 15. CA1822 + MemberCanBeMadeStatic.Local
// ============================================================
{
  const fp = path.join(srcRoot, 'tests/IncrementalParityTests.cs');
  let c = readFile(fp);
  c = c.replace('    private List<EdgeRecord> GetEdgesByKindAndProvenance(', '    private static List<EdgeRecord> GetEdgesByKindAndProvenance(');
  c = c.replace('    private List<EdgeRecord> GetMayDispatchEdges(', '    private static List<EdgeRecord> GetMayDispatchEdges(');
  writeFile(fp, c);
  console.log('Fixed CA1822 + MemberCanBeMadeStatic.Local');
}

// ============================================================
// 16. UseNameOfInsteadOfToString (EdgeKind.X.ToString() → nameof(EdgeKind.X))
// ============================================================
const files = execSync(`rg -l "EdgeKind\\." -g "*.cs" "${path.join(srcRoot, 'src')}" "${path.join(srcRoot, 'tests')}"`, { encoding: 'utf8' }).trim().split('\n');
let nameofCount = 0;
for (const file of files) {
  const fp = file.trim();
  if (!fp) continue;
  let c = readFile(fp);
  const original = c;
  c = c.replace(/EdgeKind\.([A-Za-z_]+)\.ToString\(\)/g, (match, name) => {
    nameofCount++;
    return `nameof(EdgeKind.${name})`;
  });
  if (c !== original) writeFile(fp, c);
}
console.log(`UseNameOfInsteadOfToString: ${nameofCount} replacements`);

console.log('\nDone! Run build to verify.');
