using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

public sealed class ReflectionExtractor
{
    private readonly ReflectionExtractionContext _context;
    public List<CompilationFactExtractor.ExtractionMeasurement> Measurements { get; } = [];

    public ReflectionExtractor(Compilation compilation, string snapshotId, string gitRoot, IReadOnlySet<string>? scopeDocuments = null, Dictionary<SyntaxTree, SemanticModel>? semanticModelCache = null)
        : this(compilation, snapshotId, gitRoot, scopeDocuments, null, semanticModelCache)
    {
    }

    internal ReflectionExtractor(Compilation compilation, string snapshotId, string gitRoot, IReadOnlySet<string>? scopeDocuments, BindingIncompletenessCollector? incompleteness, Dictionary<SyntaxTree, SemanticModel>? semanticModelCache = null, IEnumerable<string>? documentPaths = null, IEnumerable<string>? generatedDocumentPaths = null)
    {
        if (compilation == null) throw new ArgumentNullException(nameof(compilation));
        if (snapshotId == null) throw new ArgumentNullException(nameof(snapshotId));
        _context = new ReflectionExtractionContext(compilation, snapshotId, gitRoot, scopeDocuments, incompleteness, semanticModelCache, documentPaths, generatedDocumentPaths);
    }

    public List<EdgeRecord> Extract()
    {
        var edges = new List<EdgeRecord>();

        var extractors = new (string Name, Func<SyntaxNode, SemanticModel, IEnumerable<EdgeRecord>> Run)[]
        {
            ("TypeOfReflectionExtractor", new TypeOfReflectionExtractor(_context).Extract),
            ("NameOfReflectionExtractor", new NameOfReflectionExtractor(_context).Extract),
            ("StringLiteralReflectionExtractor", new StringLiteralReflectionExtractor(_context).Extract),
            ("UnknownPatternReflectionExtractor", new UnknownPatternReflectionExtractor(_context).Extract),
        };
        var measurements = new (long ElapsedMs, long AllocatedBytes)[extractors.Length];

        foreach (var syntaxTree in _context.Compilation.SyntaxTrees)
        {
            // Skip syntax trees not in scope (when scoping is active)
            if (_context.ScopeDocuments != null)
            {
                var filePath = syntaxTree.FilePath;
                if (string.IsNullOrEmpty(filePath) || !_context.ScopeDocuments.Contains(PathNormalizer.ToForwardSlash(filePath)))
                    continue;
            }

            // Roots stay transient per tree instead of materializing every root
            // and semantic model for the whole compilation before extracting.
            var root = syntaxTree.GetRoot();
            var semanticModel = _context.GetOrCreateSemanticModel(syntaxTree);

            for (int i = 0; i < extractors.Length; i++)
            {
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                edges.AddRange(extractors[i].Run(root, semanticModel));
                stopwatch.Stop();
                measurements[i].ElapsedMs += stopwatch.ElapsedMilliseconds;
                measurements[i].AllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            }
        }

        for (int i = 0; i < extractors.Length; i++)
        {
            Measurements.Add(new CompilationFactExtractor.ExtractionMeasurement(
                extractors[i].Name,
                measurements[i].ElapsedMs,
                measurements[i].AllocatedBytes));
        }

        return edges;
    }
}
