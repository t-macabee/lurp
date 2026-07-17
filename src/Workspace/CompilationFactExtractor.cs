using System;
using System.Collections.Generic;
using Lurp.Adapters;
using Lurp.Storage;
using Microsoft.CodeAnalysis;

namespace Lurp;

/// <summary>
/// Single entry point for the full extraction pipeline over a <see cref="Compilation"/>.
/// Every call site that runs SymbolExtractor → MemberEdgeExtractor → PolymorphismExtractor
/// → ReflectionExtractor → adapters → diagnostics MUST use this class so the sequence
/// stays in sync across full-index, incremental, cross-document, and test paths.
/// </summary>
public static class CompilationFactExtractor
{
    public sealed record ExtractionResult(
        List<SymbolDeclaration> Declarations,
        List<EdgeRecord> Edges,
        List<DiagnosticRecord> Diagnostics);

    /// <summary>
    /// Run the complete extraction pipeline against <paramref name="compilation"/>.
    /// </summary>
    /// <param name="compilation">The Roslyn compilation to analyse.</param>
    /// <param name="workspaceInfo">Document content / version / generated-doc metadata.</param>
    /// <param name="snapshotId">Snapshot identifier stamped onto every output record.</param>
    /// <param name="projectName">Project name used when labelling diagnostics.</param>
    /// <param name="skipAdapters">Optional set of adapter names to exclude.</param>
    /// <param name="logWarning">Optional callback for non-fatal extraction warnings (e.g. reflection failure).</param>
    /// <param name="logError">Optional callback for adapter-level errors.</param>
    public static ExtractionResult ExtractAll(
        Compilation compilation,
        WorkspaceInfo workspaceInfo,
        string snapshotId,
        string projectName,
        IReadOnlySet<string>? skipAdapters = null,
        Action<string>? logWarning = null,
        Action<string>? logError = null)
    {
        // 1. SymbolExtractor: produces declarations AND type-level edges.
        var symbolExtractor = new SymbolExtractor(
            compilation,
            workspaceInfo.DocumentContents,
            workspaceInfo.Documents,
            workspaceInfo.GeneratedDocuments,
            snapshotId);

        var declarations = symbolExtractor.ExtractAll();
        var edges = symbolExtractor.ExtractEdges();

        // 2. MemberEdgeExtractor: field/method/property cross-references.
        var memberEdgeExtractor = new MemberEdgeExtractor(
            compilation, workspaceInfo.Documents, workspaceInfo.GeneratedDocuments, snapshotId);
        edges.AddRange(memberEdgeExtractor.ExtractAll());

        // 3. PolymorphismExtractor: may_dispatch_to / statically_calls edges.
        var polyExtractor = new PolymorphismExtractor(compilation, snapshotId);
        edges.AddRange(polyExtractor.ExtractAll());

        // 4. ReflectionExtractor — best-effort; failure must not block the pipeline.
        try
        {
            var reflectionExtractor = new ReflectionExtractor(compilation, snapshotId);
            edges.AddRange(reflectionExtractor.Extract());
        }
        catch (Exception ex)
        {
            logWarning?.Invoke($"Reflection extraction failed: {ex.Message}");
        }

        // 5. Framework adapters — best-effort per adapter.
        var adapters = AdapterRegistry.GetAdapters(skipAdapters);
        foreach (var adapter in adapters)
        {
            try
            {
                edges.AddRange(adapter.Extract(compilation, snapshotId));
            }
            catch (Exception ex)
            {
                logError?.Invoke($"Adapter '{adapter.Name}' failed: {ex.Message}");
            }
        }

        // 6. Diagnostics.
        var diagnostics = CompilationHelper.GetDiagnostics(projectName, compilation);

        return new ExtractionResult(declarations, edges, diagnostics);
    }
}
