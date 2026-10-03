using CallShapes.App;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ShapeService>();
builder.Services.AddHostedService<ShapeHostedService>();
builder.Services.AddOptions<ShapeOptions>().Configure(options => options.Value = CoreApi.OptionsTarget());

var app = builder.Build();
app.MapGet("/shape", () => CoreApi.MinimalApiTarget());

var topLevelValue = CoreApi.TopLevelTarget();
Console.WriteLine($"top-level {topLevelValue}");

if (args.Length > 0 && args[0] == "--run")
{
    app.Run();
}
