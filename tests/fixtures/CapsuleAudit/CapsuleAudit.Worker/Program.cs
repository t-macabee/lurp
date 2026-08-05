using CapsuleAudit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CapsuleAudit.Worker;

public static class Program
{
    public static void Main(string[] args)
    {
        var services = new ServiceCollection();
        // Finding 1: second host calling the same shared registration extension.
        // The outbox publisher is registered in both hosts through this one call.
        services.AddApplicationServices();
    }
}
