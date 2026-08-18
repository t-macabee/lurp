const fs = require('fs');
const path = require('path');

const srcRoot = 'C:\\Users\\Tarik\\Desktop\\lurp';

function readFile(p) { return fs.readFileSync(p, 'utf8'); }
function writeFile(p, c) { fs.writeFileSync(p, c, 'utf8'); }

// Pattern 1: Array.Empty<T>() → []
// Pattern 2: new HashSet<T>() in return/assignment with typed context → []
// Pattern 3: new List<T>() → [] when target type is explicit (not var)
// Pattern 4: new List<T> { items } → [items] when target type is explicit

const fixes = [
  // Handlers/TimingsHandler.cs - Array.Empty<object>() → []
  { file: 'src/Handlers/TimingsHandler.cs', 
    old: 'timings = Array.Empty<object>()', 
    nw: 'timings = []' },
  
  // SnapshotManifest.cs:136 - Array.Empty<byte>() → []
  { file: 'src/Workspace/SnapshotManifest.cs',
    old: 'content ?? Array.Empty<byte>()',
    nw: 'content ?? []' },
  
  // ImpactTraverser.cs:22 - new List<ImpactHop>() and new HashSet<string> in constructor arg
  // new HashSet<string> { symbolId } - already has target type from Queue construction
  { file: 'src/Workspace/ImpactTraverser.cs',
    old: 'new Queue<(string currentId, List<ImpactHop> hops, HashSet<string> visited)>();\n        queue.Enqueue((symbolId, new List<ImpactHop>(), new HashSet<string> { symbolId }));',
    nw: 'new Queue<(string currentId, List<ImpactHop> hops, HashSet<string> visited)>();\n        queue.Enqueue((symbolId, [], new HashSet<string> { symbolId }));' },

  // EdgeMerge.cs:27 - new List<EdgeRecord>(best.Values) → [..best.Values]
  { file: 'src/Storage/EdgeMerge.cs',
    old: 'return new List<EdgeRecord>(best.Values);',
    nw: 'return [..best.Values];' },

  // AnnotationHandler.cs:16 - new[] { annotation } → [annotation]
  { file: 'src/Handlers/AnnotationHandler.cs',
    old: 'store.SaveAnnotations(snapshotId, new[] { annotation });',
    nw: 'store.SaveAnnotations(snapshotId, [annotation]);' },

  // IncrementalParityTests.cs:832 - new[] { injectedRow } → [injectedRow]
  { file: 'tests/IncrementalParityTests.cs',
    old: 'store.SaveBindingIncompleteness(snapshotA, new[] { injectedRow });',
    nw: 'store.SaveBindingIncompleteness(snapshotA, [injectedRow]);' },

  // CompilationFactExtractor.cs:100 - new List<SymbolDeclaration>() → []
  { file: 'src/Workspace/CompilationFactExtractor.cs',
    old: 'symbolExtractor.ExtractAll,\n             new List<SymbolDeclaration>());',
    nw: 'symbolExtractor.ExtractAll,\n             []);' },

  // CompilationFactExtractor.cs:106 - new List<EdgeRecord>() → []
  { file: 'src/Workspace/CompilationFactExtractor.cs',
    old: 'symbolExtractor.ExtractEdges,\n             new List<EdgeRecord>());',
    nw: 'symbolExtractor.ExtractEdges,\n             []);' },

  // CompilationFactExtractorRunStageTests.cs:10 - new List<CompilationFactExtractor.ExtractionFailure>() → []
  { file: 'tests/CompilationFactExtractorRunStageTests.cs',
    old: 'new CompilationFactExtractor.StageContext(projectName,\n             new List<CompilationFactExtractor.ExtractionFailure>(),',
    nw: 'new CompilationFactExtractor.StageContext(projectName,\n             [],' },

  // CompilationFactExtractorRunStageTests.cs:75 - new List<string>() → []
  { file: 'tests/CompilationFactExtractorRunStageTests.cs',
    old: 'new List<string>());',
    nw: '[]);' },

  // CompilationFactExtractorRunStageTests.cs:91,92 - new List<int> { 1,2,3 } → [1,2,3] and new List<int>() → []
  { file: 'tests/CompilationFactExtractorRunStageTests.cs',
    old: '() => new List<int> { 1, 2, 3 },\n             new List<int>());',
    nw: '() => new List<int> { 1, 2, 3 },\n             []);' },

  // CompilationFactExtractorRunStageTests.cs:94 - Assert.Equal(new List<int> { 1, 2, 3 }, result) → [1,2,3]
  { file: 'tests/CompilationFactExtractorRunStageTests.cs',
    old: 'Assert.Equal(new List<int> { 1, 2, 3 }, result);',
    nw: 'Assert.Equal(new List<int> { 1, 2, 3 }, result);' },  // Keep as-is: assert target might need List<T>
];

let count = 0;
for (const fix of fixes) {
  const fp = path.join(srcRoot, fix.file);
  let c = readFile(fp);
  if (c.includes(fix.old)) {
    c = c.replace(fix.old, fix.nw);
    writeFile(fp, c);
    count++;
    console.log('Fixed:', fix.file);
  } else {
    console.log('SKIP (not found):', fix.file, fix.old.substring(0, 50));
  }
}
console.log('Total:', count);
