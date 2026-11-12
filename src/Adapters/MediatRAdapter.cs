using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class MediatRAdapter : IFrameworkAdapter
{
    private const string AdapterVersion = "mediatr-v1";

    public string Name => "MediatR";
    public string Version => AdapterVersion;
    public string Description => "MediatR framework edges (request/handler)";

    private static readonly HashSet<string> UnmodeledInterfaceNames = new(StringComparer.Ordinal)
    {
        "IStreamRequestHandler", "IAsyncStreamHandler",
        "IPipelineBehavior",
        "IRequestExceptionHandler",
        "IRequestPreProcessor", "IRequestPostProcessor"
    };

    public AdapterExtractionResult Extract(AdapterExtractionContext context)
    {
        var edges = new List<EdgeRecord>();
        var allTypes = context.GetAllNamedTypes().ToList();

        var hasMediatRReferences = context.Compilation.ReferencedAssemblyNames.Any(a => a.Name.Contains("MediatR", StringComparison.OrdinalIgnoreCase));

        if (!hasMediatRReferences)
            return edges;

        var handlerTypes = CollectHandlerTypes(allTypes);

        foreach (var (handlerType, requestType) in handlerTypes)
        {
            // The Handles edge is anchored to the handler declaration, so the handler
            // is what the delete scope will have removed.
            if (!context.IsSymbolInScope(handlerType))
                continue;

            EmitHandlesEdge(handlerType, requestType, context, edges);
        }

        // Collect annotations for unmodeled MediatR patterns (no edge emitted, but
        // signal the consumer that the pattern was seen).
        var unmodeledAnnotations = new List<AnnotationRecord>();

        foreach (var type in allTypes)
        {
            if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct)
                continue;
            foreach (var iface in type.AllInterfaces)
            {
                var ifaceName = iface.OriginalDefinition?.Name;
                if (ifaceName is null || !UnmodeledInterfaceNames.Contains(ifaceName))
                    continue;
                var typeSymbolId = context.MakeSymbolId(type);
                if (typeSymbolId is null) continue;
                var (path, _, _, _, _) = context.LocationResolver.Resolve(type);
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

                switch (ifaceName)
                {
                    case "IRequestHandler":
                    {
                        var requestTypeArg = iface.TypeArguments.FirstOrDefault();
                        if (requestTypeArg is INamedTypeSymbol namedRequest)
                            handlerTypes.Add((type, namedRequest));
                        break;
                    }
                    case "INotificationHandler":
                    {
                        var notificationTypeArg = iface.TypeArguments.FirstOrDefault();
                        if (notificationTypeArg is INamedTypeSymbol namedNotification)
                            handlerTypes.Add((type, namedNotification));
                        break;
                    }
                }
            }
        }

        return handlerTypes;
    }

    private static void EmitHandlesEdge(INamedTypeSymbol handlerType, INamedTypeSymbol requestType, AdapterExtractionContext context, List<EdgeRecord> edges)
    {
        var requestId = context.MakeSymbolId(requestType);
        if (requestId == null)
            return;

        var handleMethod = handlerType.GetMembers()
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.Name == "Handle");

        if (handleMethod == null)
            return;

        var handleMethodId = context.MakeSymbolId(handleMethod);
        if (handleMethodId == null)
            return;

        edges.Add(context.MakeEdge(requestId, handleMethodId, nameof(EdgeKind.Handles), Provenance.FrameworkDerived, AdapterVersion,
            context.LocationResolver.Resolve(handleMethod)));
    }
}
