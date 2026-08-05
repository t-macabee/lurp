using CapsuleAudit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CapsuleAudit.Api;

public static class Program
{
    public static void Main(string[] args)
    {
        var services = new ServiceCollection();
        // Finding 1: first host calling the shared registration extension that
        // AddHostedService<RentalNotificationOutboxPublisher>() lives in.
        services.AddApplicationServices();
    }
}
