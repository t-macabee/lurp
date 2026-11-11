using Microsoft.CodeAnalysis;
using System.Diagnostics;

namespace Lurp.Workspace;

public sealed class ReflectionExtractor
{
    private readonly ReflectionExtractionContext _context;

    internal ReflectionExtractor(ProjectExtractionInputs inputs)
    {
        _context = new ReflectionExtractionContext(inputs);
    }

    public List<CompilationFactExtractor.ExtractionMeasurement> Measurements { get; } = [];

    public List<EdgeRecord> Extract()
    {
        var edges = new List<EdgeRecord>();

        var extractors = new (string Name, Func<SyntaxNode, SemanticModel, IEnumerable<EdgeRecord>> Run)[]
        {
            ("TypeOfReflectionExtractor", new TypeOfReflectionExtractor(_context).Extract),
            ("NameOfReflectionExtractor", new NameOfReflectionExtractor(_context).Extract),
            ("StringLiteralReflectionExtractor", new StringLiteralReflectionExtractor(_context).Extract),
            ("UnknownPatternReflectionExtractor", new UnknownPatternReflectionExtractor(_context).Extract)
        };
        var measurements = new (long ElapsedMs, long AllocatedBytes)[extractors.Length];

        foreach (var syntaxTree in _context.Compilation.SyntaxTrees)
        {
            if (!_context.IsInScope(syntaxTree))
                continue;

            // Roots stay transient per tree instead of materializing every root
            // and semantic model for the whole compilation before extracting.
            var root = syntaxTree.GetRoot();
            var semanticModel = _context.GetOrCreateSemanticModel(syntaxTree);

            for (var i = 0; i < extractors.Length; i++)
            {
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var stopwatch = Stopwatch.StartNew();
                edges.AddRange(extractors[i].Run(root, semanticModel));
                stopwatch.Stop();
                measurements[i].ElapsedMs += stopwatch.ElapsedMilliseconds;
                measurements[i].AllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            }
        }

        for (var i = 0; i < extractors.Length; i++)
            Measurements.Add(new CompilationFactExtractor.ExtractionMeasurement(
                extractors[i].Name,
                measurements[i].ElapsedMs,
                measurements[i].AllocatedBytes));

        return edges;
    }
}