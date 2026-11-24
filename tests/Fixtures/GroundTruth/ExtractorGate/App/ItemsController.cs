using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ExtractorGate.App;

[Route("api/[controller]")]
public class ItemsController : ControllerBase
{
    [HttpGet("{id}")]
    public Item Get(int id) => new Item { Id = id };
}

public class Item
{
    public int Id { get; set; }
}

public static class Pipeline
{
    public static void Configure(IApplicationBuilder app) => app.UseMiddleware<GateMiddleware>();
}

public sealed class GateMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context) => next(context);
}
