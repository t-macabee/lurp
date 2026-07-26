using Outcome.Contracts;
using Outcome.Validation;

namespace Microsoft.Extensions.DependencyInjection
{
    public interface IServiceCollection { }

    public static class ServiceCollectionServiceExtensions
    {
        public static IServiceCollection AddScoped<TService, TImplementation>(
            this IServiceCollection services)
            where TImplementation : TService => services;

        public static IServiceCollection AddHostedService<TImplementation>(
            this IServiceCollection services) => services;
    }
}

namespace Outcome.Composition
{
    using Microsoft.Extensions.DependencyInjection;

    public sealed class BackgroundRefreshService { }

    public static class ServiceRegistration
    {
        public static IServiceCollection Configure(IServiceCollection services)
        {
            services.AddScoped<IOrderValidator, StrictOrderValidator>();
            services.AddHostedService<BackgroundRefreshService>();
            return services;
        }
    }
}
