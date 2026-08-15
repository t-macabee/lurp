using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class MediatRAdapter : IFrameworkAdapter
{
    public string Name => "MediatR";
    public string Version => "mediatr-v1";
    public string Description => "MediatR framework edges (request/handler)";

    public AdapterExtractionResult Extract(AdapterExtractionContext context)
    {
        var compilation = context.Compilation;
        var snapshotId = context.SnapshotId;
        var locationResolver = context.LocationResolver;
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();
        var assemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
        var allTypes = AdapterTypeUtils.GetAllNamedTypes(compilation.Assembly.GlobalNamespace);

        bool hasMediatRReferences = compilation.ReferencedAssemblyNames.Any(a => a.Name.Contains("MediatR", StringComparison.OrdinalIgnoreCase));

        if (!hasMediatRReferences)
            return edges;

        var handlerTypes = CollectHandlerTypes(allTypes);

        foreach (var (handlerType, requestType) in handlerTypes)
        {
            // The Handles edge is anchored to the handler declaration, so the handler
            // is what the delete scope will have removed.
            if (!context.IsSymbolInScope(handlerType))
                continue;

            EmitHandlesEdge(handlerType, requestType, assemblyIdentity, snapshotId, edges, seen, locationResolver);
        }

        // Collect annotations for unmodeled MediatR patterns (no edge emitted, but
        // signal the consumer that the pattern was seen).
        var unmodeledAnnotations = new List<AnnotationRecord>();
        var unmodeledIfaceNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "IStreamRequestHandler", "IAsyncStreamHandler",
            "IPipelineBehavior",
            "IRequestExceptionHandler",
            "IRequestPreProcessor", "IRequestPostProcessor",
        };

        foreach (var type in allTypes)
        {
            if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct)
                continue;
            foreach (var iface in type.AllInterfaces)
            {
                var ifaceName = iface.OriginalDefinition?.Name;
                if (ifaceName is null || !unmodeledIfaceNames.Contains(ifaceName))
                    continue;
                var typeSymbolId = SymbolIdFactory.Make(type, assemblyIdentity);
                if (typeSymbolId is null) continue;
                var (path, _, _, _, _) = locationResolver.Resolve(type);
                unmodeledAnnotations.Add(new AnnotationRecord(
                    typeSymbolId, "unmodeled_mediatr_pattern", ifaceName, path));
                break; // one annotation per type is enough
            }
        }

        return new AdapterExtractionResult(edges, unmodeledAnnotations);
    }

    private static List<(INamedTypeSymbol HandlerType, INamedTypeSymbol RequestType)> CollectHandlerTypes(List<INamedTypeSymbol> allTypes)
    {
        var handlerTypes = new List<(INamedTypeSymbol HandlerType, INamedTypeSymbol RequestType)>();

        foreach (var type in allTypes)
        {
            if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct)
                continue;

            foreach (var iface in type.AllInterfaces)
            {
                var ifaceName = iface.OriginalDefinition?.Name;

                if (ifaceName == "IRequestHandler")
                {
                    var requestTypeArg = iface.TypeArguments.FirstOrDefault();
                    if (requestTypeArg is INamedTypeSymbol namedRequest)
                        handlerTypes.Add((type, namedRequest));
                }

                if (ifaceName == "INotificationHandler")
                {

                    var notificationTypeArg = iface.TypeArguments.FirstOrDefault();
                    if (notificationTypeArg is INamedTypeSymbol namedNotification)
                        handlerTypes.Add((type, namedNotification));
                }
            }
        }

        return handlerTypes;
    }

    private static void EmitHandlesEdge(INamedTypeSymbol handlerType, INamedTypeSymbol requestType, string assemblyIdentity, string snapshotId,
        List<EdgeRecord> edges, HashSet<(string source, string target, string kind)> seen, EdgeLocationResolver locationResolver)
    {
        var requestId = SymbolIdFactory.Make(requestType, assemblyIdentity);
        if (requestId == null)
            return;

        var handleMethod = handlerType.GetMembers()
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.Name == "Handle");

        if (handleMethod == null)
            return;

        var handleMethodId = SymbolIdFactory.Make(handleMethod, assemblyIdentity);
        if (handleMethodId == null)
            return;

        var key = (requestId, handleMethodId, EdgeKind.Handles.ToString());
        if (seen.Add(key))
        {
            var (path, sl, sc, el, ec) = locationResolver.Resolve(handleMethod);

            edges.Add(new EdgeRecord
            {
                SourceSymbolId = requestId,
                TargetSymbolId = handleMethodId,
                Kind = EdgeKind.Handles.ToString(),
                Provenance = Provenance.FrameworkDerived,
                SnapshotId = snapshotId,
                ExtractorVersion = "mediatr-v1",
                SourceDocumentPath = path,
                SourceStartLine = sl,
                SourceStartColumn = sc,
                SourceEndLine = el,
                SourceEndColumn = ec,
                IsCrossGenerated = locationResolver.IsGenerated(path),
            });
        }
    }

}
