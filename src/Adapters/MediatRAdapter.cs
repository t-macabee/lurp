using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class MediatRAdapter : IFrameworkAdapter
{
    private const string AdapterVersion = "mediatr-v2";

    public string Name => "MediatR";
    public string Version => AdapterVersion;
    public string Description => "MediatR framework edges (request/handler)";

    private static readonly string[] UnmodeledInterfaceMetadataNames =
    [
        "MediatR.IStreamRequestHandler`2",
        "MediatR.IPipelineBehavior`2",
        "MediatR.IStreamPipelineBehavior`2",
        "MediatR.Pipeline.IRequestPreProcessor`1",
        "MediatR.Pipeline.IRequestPostProcessor`2",
        "MediatR.Pipeline.IRequestExceptionHandler`3",
        "MediatR.Pipeline.IRequestExceptionAction`2"
    ];

    public AdapterExtractionResult Extract(AdapterExtractionContext context)
    {
        var edges = new List<EdgeRecord>();
        var unmodeledAnnotations = new List<AnnotationRecord>();

        foreach (var type in context.GetAllNamedTypes())
        {
            if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct)
                continue;

            if (!context.IsSymbolInScope(type))
                continue;

            if (!type.IsAbstract)
            {
                foreach (var iface in type.AllInterfaces)
                {
                    if (!FrameworkTypes.Is(iface, "MediatR.IRequestHandler`2") &&
                        !FrameworkTypes.Is(iface, "MediatR.IRequestHandler`1") &&
                        !FrameworkTypes.Is(iface, "MediatR.INotificationHandler`1"))
                    {
                        continue;
                    }

                    if (iface.TypeArguments.FirstOrDefault() is not INamedTypeSymbol requestType)
                        continue;

                    EmitHandlesEdge(type, iface, requestType, context, edges);
                }
            }

            foreach (var iface in type.AllInterfaces)
            {
                if (!IsUnmodeledInterface(iface))
                    continue;

                var ifaceName = iface.OriginalDefinition?.Name;
                if (ifaceName is null)
                    continue;

                var typeSymbolId = context.MakeSymbolId(type);
                if (typeSymbolId is null)
                    continue;

                var (path, _, _, _, _) = context.LocationResolver.Resolve(type);
                unmodeledAnnotations.Add(new AnnotationRecord(
                    typeSymbolId, "unmodeled_mediatr_pattern", ifaceName, path));
                break; // one annotation per type is enough
            }
        }

        return new AdapterExtractionResult(edges, unmodeledAnnotations);
    }

    private static bool IsUnmodeledInterface(INamedTypeSymbol iface)
    {
        foreach (var metadataName in UnmodeledInterfaceMetadataNames)
        {
            if (FrameworkTypes.Is(iface, metadataName))
                return true;
        }

        return false;
    }

    private static void EmitHandlesEdge(
        INamedTypeSymbol handlerType,
        INamedTypeSymbol handlerInterface,
        INamedTypeSymbol requestType,
        AdapterExtractionContext context,
        List<EdgeRecord> edges)
    {
        var requestId = context.MakeSymbolId(requestType);
        if (requestId == null)
            return;

        var interfaceHandle = handlerInterface.GetMembers("Handle").OfType<IMethodSymbol>().FirstOrDefault();
        if (interfaceHandle == null)
            return;

        if (handlerType.FindImplementationForInterfaceMember(interfaceHandle) is not IMethodSymbol handleMethod)
            return;

        var handleMethodId = context.MakeSymbolId(handleMethod.OriginalDefinition);
        if (handleMethodId == null)
            return;

        var location = SymbolEqualityComparer.Default.Equals(handleMethod.ContainingType, handlerType)
            ? context.LocationResolver.Resolve(handleMethod)
            : context.LocationResolver.Resolve(handlerType);

        edges.Add(context.MakeEdge(requestId, handleMethodId, nameof(EdgeKind.Handles), Provenance.FrameworkDerived, AdapterVersion, location));
    }
}
