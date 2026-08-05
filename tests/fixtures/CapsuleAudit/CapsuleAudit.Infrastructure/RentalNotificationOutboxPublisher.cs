using CapsuleAudit.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace CapsuleAudit.Infrastructure;

// Finding 1 anchor: a hosted service registered through a generic-argument
// AddHostedService<T> form in a shared extension method invoked by BOTH hosts.
// Finding 2 anchor: derives from BackgroundService and overrides ExecuteAsync,
// the framework-invoked entry point with a StopHost exception contract.
public sealed class RentalNotificationOutboxPublisher : BackgroundService, IHostedService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            return ProcessBatchAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }
        catch (Exception)
        {
            // Rethrow: default BackgroundServiceExceptionBehavior.StopHost. The
            // capsule must surface this as a framework entry point, not a caller,
            // and the rethrow->StopHost contract is the audit's Finding 2.
            throw;
        }
    }

    private Task ProcessBatchAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}

public static class DependencyInjection
{
    // Shared registration extension called by both eNote.API/Program.cs and
    // eNote.Worker/Program.cs. AddHostedService<T> is the generic-argument
    // registration form the DI adapter does not model (audit Finding 1).
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddHostedService<RentalNotificationOutboxPublisher>();
        return services;
    }
}
