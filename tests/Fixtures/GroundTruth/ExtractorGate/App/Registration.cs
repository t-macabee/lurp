using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ExtractorGate.App;

public interface IService { }

public class Service : IService { }

public interface IClock { }

public class Clock : IClock { }

public static class Registration
{
    public static void Configure(IServiceCollection services)
    {
        services.AddScoped<IService, Service>();
        services.Configure<GateOptions>(_ => { });
        services.AddHttpClient<GateClient>();
        services.TryAddSingleton<IClock, Clock>();
        services.AddKeyedScoped<IService, Service>("keyed");
        services.AddScoped<IClock>(_ => new Clock());
        services.AddSingleton<IClock>(new Clock());
        services.Add(ServiceDescriptor.Transient<IService, Service>());
        services.Scan(scan => scan
            .FromAssemblyOf<Service>()
            .AddClasses()
            .AsImplementedInterfaces()
            .WithScopedLifetime());
    }
}

public class GateOptions;

public class GateClient(HttpClient client)
{
    public HttpClient Client { get; } = client;
}
