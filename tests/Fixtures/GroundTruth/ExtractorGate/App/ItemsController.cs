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
