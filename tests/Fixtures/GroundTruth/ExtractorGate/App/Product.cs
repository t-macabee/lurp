using System.Text.Json.Serialization;

namespace ExtractorGate.App;

public class Product
{
    [JsonPropertyName("product_name")]
    public string Name { get; set; } = "";
}

[JsonSerializable(typeof(Product))]
internal partial class GateJsonContext
{
}
