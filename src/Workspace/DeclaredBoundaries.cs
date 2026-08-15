namespace Lurp.Workspace;

internal static class DeclaredBoundaries
{
    internal sealed record BoundaryEntry(
        string Id,
        string ConstructClass,
        string Description,
        string UncertaintyReason
    );

    internal static readonly IReadOnlyList<BoundaryEntry> Known =
    [
        new(
            Id: "di_hosted_service",
            ConstructClass: "AddHostedService<T>",
            Description:
                "ServiceCollection.AddHostedService<T>() registers a type for the hosted-service lifecycle. " +
                "The concrete type is resolved but the runtime activation semantics (scoped dependency resolution, " +
                "IHostedService.StartAsync/StopAsync sequencing, parallel start) are not captured.",
            UncertaintyReason:
                "Hosted-service registration form AddHostedService<T> is not fully modeled: the concrete type " +
                "is resolved but the runtime activation semantics of the hosted-service lifecycle are not captured."
        ),
        new(
            Id: "di_options",
            ConstructClass: "Configure<T> / AddOptions<T>",
            Description:
                "Options-pattern methods (Configure<T>, AddOptions<T>) bind configuration sections to options types. " +
                "The options type is resolved but the configuration-binding mechanics (IConfiguration section binding, " +
                "IOptions<T>/IOptionsSnapshot<T> resolution) are not modeled.",
            UncertaintyReason:
                "Options-pattern registration Configure<T>/AddOptions<T> is not fully modeled: the options type is " +
                "resolved but the configuration-binding semantics are not captured."
        ),
        new(
            Id: "di_external_extension",
            ConstructClass: "External IServiceCollection extension methods",
            Description:
                "Extension methods defined in external assemblies that extend IServiceCollection (e.g. " +
                "AddEntityFrameworkStores<TContext> from Microsoft.AspNetCore.Identity) are identified " +
                "by signature but their registration semantics cannot be analyzed without the source.",
            UncertaintyReason:
                "An external IServiceCollection extension method was detected but could not be analyzed: " +
                "the method lives outside the compilation so its registration semantics are unknown."
        ),
        new(
            Id: "masstransit_consumer",
            ConstructClass: "MassTransit consumer registration",
            Description:
                "MassTransit consumer registration forms (AddConsumer<T>, AddConsumer<TConsumer,TDefinition>, " +
                "EndpointsConventionRegistry, endpoint configuration via ReceiveEndpoint) wire consumer " +
                "classes into the message-bus pipeline. No MassTransit adapter exists, so consumer wiring " +
                "edges are never emitted.",
            UncertaintyReason:
                "MassTransit consumer registration is not modeled: no adapter exists to emit consumer-wiring " +
                "edges for AddConsumer or endpoint configuration."
        ),
        new(
            Id: "ef_convention",
            ConstructClass: "EF Core model conventions beyond query filters and indexes",
            Description:
                "EF Core model building in OnModelCreating/IEntityTypeConfiguration<T>.Configure expresses " +
                "storage/persistence semantics via the fluent API beyond HasQueryFilter and HasIndex: " +
                "Property().IsRequired(), Property().HasMaxLength(), HasDefaultSchema(), HasColumnType(), " +
                "and value-conversion declarations. These declarative constraints are not modeled.",
            UncertaintyReason:
                "EF Core model conventions beyond query filters and indexes (e.g. IsRequired, HasMaxLength, " +
                "HasDefaultSchema) are not modeled."
        ),
        new(
            Id: "shape_similarity",
            ConstructClass: "Semantic sibling similarity",
            Description:
                "Lurp's graph is call/declare/implement-shaped and grounded in compiler-proved relations. " +
                "Similarity between implementations (shared collaborator sets, naming patterns such as " +
                "'*ForStoreAsync') is an inferred ranking: it has no compiler oracle, no provenance to " +
                "attach, and no completeness claim that a full rebuild could verify. It is deliberately " +
                "not modeled.",
            UncertaintyReason:
                "Semantic sibling similarity is not modeled: consistency audits requiring comparison against " +
                "'similar' implementations are unsupported. Use the proved neighborhood instead — " +
                "implementations of a shared interface, shared base types and overrides, callers/callees, " +
                "and containing-declaration siblings."
        ),
        new(
            Id: "mediatr_stream_handler",
            ConstructClass: "IStreamRequestHandler / IAsyncStreamHandler",
            Description: "MediatR stream request handler patterns (IStreamRequestHandler<TRequest,TResponse> and " +
                         "IAsyncStreamHandler<TRequest,TResponse>) are not modeled: no Handles edge is emitted.",
            UncertaintyReason:
                "MediatR stream handler pattern (IStreamRequestHandler or IAsyncStreamHandler) is not modeled: " +
                "the implementing type was detected but no Handles edge was emitted."
        ),
        new(
            Id: "mediatr_pipeline_behavior",
            ConstructClass: "IPipelineBehavior",
            Description: "MediatR pipeline behavior (IPipelineBehavior<TRequest,TResponse>) intercepts the request " +
                         "pipeline. No adapter models this pattern; no edge is emitted.",
            UncertaintyReason:
                "MediatR pipeline behavior (IPipelineBehavior) is not modeled: the implementing type was detected " +
                "but no edge was emitted. Pipeline behaviors affect all requests passing through the pipeline."
        ),
        new(
            Id: "mediatr_exception_handler",
            ConstructClass: "IRequestExceptionHandler",
            Description: "MediatR request-exception handler (IRequestExceptionHandler<TRequest,TResponse,TException>) " +
                         "is not modeled: no edge is emitted for exception-handler types.",
            UncertaintyReason:
                "MediatR exception handler (IRequestExceptionHandler) is not modeled: the implementing type was " +
                "detected but no edge was emitted."
        ),
        new(
            Id: "mediatr_pre_post_processor",
            ConstructClass: "IRequestPreProcessor / IRequestPostProcessor",
            Description: "MediatR pre- and post-processors (IRequestPreProcessor<TRequest>, " +
                         "IRequestPostProcessor<TRequest,TResponse>) are not modeled: no edge is emitted.",
            UncertaintyReason:
                "MediatR pre/post processor (IRequestPreProcessor or IRequestPostProcessor) is not modeled: " +
                "the implementing type was detected but no edge was emitted."
        ),
    ];

    internal static BoundaryEntry? FindById(string id)
        => Known.FirstOrDefault(entry => entry.Id == id);

    internal static string UncertaintyDescription(string edgeKind)
        => $"Unmodeled construct: a '{edgeKind}' edge carries 'runtime_unknown' provenance because the " +
           "construct is listed in DeclaredBoundaries.Known as deliberately not fully modeled. " +
           "The concrete type was resolved but the runtime activation/registration semantics are not captured. " +
           $"See DeclaredBoundaries.Known for the full, closed list of declared boundaries ({Known.Count} entries).";
}
