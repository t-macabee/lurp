using Microsoft.Extensions.DependencyInjection;

namespace ExtractorGate.App;

public interface IService { }

public class Service : IService { }

public static class Registration
{
    public static void Configure(IServiceCollection services)
    {
        services.AddScoped<IService, Service>();
        services.Scan(scan => scan
            .FromAssembliesOf<Service>()
            .AddClasses()
            .AsImplementedInterfaces()
            .WithScopedLifetime());
    }
}
