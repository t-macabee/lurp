namespace Lurp.Workspace;

internal static class DeclaredBoundaries
{
    internal static readonly IReadOnlyList<BoundaryEntry> Known =
    [
        new(
            "di_hosted_service",
            "Hosted-service registration form AddHostedService<T> is not fully modeled: the concrete type " +
            "is resolved but the runtime activation semantics of the hosted-service lifecycle are not captured."
        ),
        new(
            "di_options",
            "Options-pattern registration Configure<T>/AddOptions<T> is not fully modeled: the options type is " +
            "resolved but the configuration-binding semantics are not captured."
        ),
        new(
            "di_external_extension",
            "An external IServiceCollection extension method was detected but could not be analyzed: " +
            "the method lives outside the compilation so its registration semantics are unknown."
        ),
        new(
            "masstransit_consumer",
            "MassTransit consumer registration is not modeled: no adapter exists to emit consumer-wiring " +
            "edges for AddConsumer or endpoint configuration."
        ),
        new(
            "ef_convention",
            "EF Core model conventions beyond query filters and indexes (e.g. IsRequired, HasMaxLength, " +
            "HasDefaultSchema) are not modeled."
        ),
        new(
            "shape_similarity",
            "Semantic sibling similarity is not modeled: consistency audits requiring comparison against " +
            "'similar' implementations are unsupported. Use the proved neighborhood instead — " +
            "implementations of a shared interface, shared base types and overrides, callers/callees, " +
            "and containing-declaration siblings."
        ),
        new(
            "mediatr_stream_handler",
            "MediatR stream handler pattern (IStreamRequestHandler or IAsyncStreamHandler) is not modeled: " +
            "the implementing type was detected but no Handles edge was emitted."
        ),
        new(
            "mediatr_pipeline_behavior",
            "MediatR pipeline behavior (IPipelineBehavior) is not modeled: the implementing type was detected " +
            "but no edge was emitted. Pipeline behaviors affect all requests passing through the pipeline."
        ),
        new(
            "mediatr_exception_handler",
            "MediatR exception handler (IRequestExceptionHandler) is not modeled: the implementing type was " +
            "detected but no edge was emitted."
        ),
        new(
            "mediatr_pre_post_processor",
            "MediatR pre/post processor (IRequestPreProcessor or IRequestPostProcessor) is not modeled: " +
            "the implementing type was detected but no edge was emitted."
        )
    ];

    internal static BoundaryEntry? FindById(string id)
    {
        return Known.FirstOrDefault(entry => entry.Id == id);
    }

    internal static string UncertaintyDescription(string edgeKind)
    {
        return $"Unmodeled construct: a '{edgeKind}' edge carries 'runtime_unknown' provenance because the " +
               "construct is listed in DeclaredBoundaries.Known as deliberately not fully modeled. " +
               "The concrete type was resolved but the runtime activation/registration semantics are not captured. " +
               $"See DeclaredBoundaries.Known for the full, closed list of declared boundaries ({Known.Count} entries).";
    }

    internal sealed record BoundaryEntry(
        string Id,
        string UncertaintyReason
    );
}