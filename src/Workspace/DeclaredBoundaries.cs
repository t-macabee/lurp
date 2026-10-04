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
        ),
        new(
            "source_generators",
            "Source-generated code is not executed or indexed (generated trees are excluded and obj/ output is " +
            "path-filtered), so symbols, calls, and registrations declared only by a source generator are " +
            "absent from the graph."
        ),
        new(
            "razor_blazor",
            "Razor (.cshtml) and Blazor components are not parsed: only C# documents in the solution's " +
            "compilations are indexed, so component event handlers and render-tree calls are absent from the graph."
        ),
        new(
            "minimal_api_endpoints",
            "Minimal API endpoints and non-controller ASP.NET Core surfaces (MapGet/MapPost, endpoint filters, " +
            "SignalR hubs, gRPC services) are not modeled: the ASP.NET Core adapter recognizes only " +
            "Controller-derived types, so endpoint-to-handler routes are absent from the graph."
        ),
        new(
            "multi_target_union",
            "Multi-target projects are indexed as one union snapshot over every declared target framework: the " +
            "declared TFM list is recorded per project and the per-symbol TFM set is recorded per snapshot, but " +
            "reads do not surface the per-symbol set, so a construct present in only one target framework cannot " +
            "be told apart by a consumer from one present in all of them."
        ),
        new(
            "non_csharp_projects",
            "F# and VB projects are out of scope: MSBuildWorkspace skips non-C# languages and the skip is " +
            "reported as a warning, so symbols and relationships declared in those projects are absent from " +
            "the graph."
        ),
        new(
            "static_abstract_dispatch",
            "Static-abstract interface dispatch is not modeled as a direct edge to the implementation: a generic " +
            "method constrained to an interface with a static abstract member is extracted as a Calls edge to " +
            "the interface member, but the implementing type selected at dispatch time is not emitted as a relation."
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