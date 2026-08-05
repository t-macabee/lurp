// DI stand-in declared in the GLOBAL Microsoft.Extensions.DependencyInjection
// namespace so `using Microsoft.Extensions.DependencyInjection;` in the
// Infrastructure / Api / Worker projects resolves to these types (mirrors the
// OutcomeBenchmark fixture's Composition project). The real DI adapter matches
// extension methods by containing-type name (ServiceCollectionServiceExtensions)
// and registration method name (AddScoped/AddHostedService), not by assembly
// identity, so these stubs are sufficient.

namespace Microsoft.Extensions.DependencyInjection
{
    public interface IServiceCollection { }

    public sealed class ServiceCollection : IServiceCollection { }

    public static class ServiceCollectionServiceExtensions
    {
        public static IServiceCollection AddScoped<TService, TImplementation>(
            this IServiceCollection services)
            where TImplementation : TService
            => services;

        public static IServiceCollection AddScoped<TService>(this IServiceCollection services)
            => services;

        public static IServiceCollection AddHostedService<TImplementation>(
            this IServiceCollection services)
            where TImplementation : class
            => services;
    }
}
