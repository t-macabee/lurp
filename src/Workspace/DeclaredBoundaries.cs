using Lurp.Storage;

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
    ];

    internal static BoundaryEntry? FindById(string id)
        => Known.FirstOrDefault(entry => entry.Id == id);

    internal static BoundaryEntry? FindByConstructClass(string constructClass)
        => Known.FirstOrDefault(entry => entry.ConstructClass == constructClass);

    internal static string UncertaintyDescription(string edgeKind)
        => $"Unmodeled construct: a '{edgeKind}' edge carries 'runtime_unknown' provenance because the " +
           "construct is listed in DeclaredBoundaries.Known as deliberately not fully modeled. " +
           "The concrete type was resolved but the runtime activation/registration semantics are not captured. " +
           $"See DeclaredBoundaries.Known for the full, closed list of declared boundaries ({Known.Count} entries).";
}
