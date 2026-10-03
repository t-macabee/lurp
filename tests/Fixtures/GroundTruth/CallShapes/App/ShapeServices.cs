using Microsoft.Extensions.Hosting;

namespace CallShapes.App;

public sealed class ShapeService
{
    public int Value => 1;
}

public sealed class ShapeOptions
{
    public int Value { get; set; }
}

public sealed class ShapeHostedService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
