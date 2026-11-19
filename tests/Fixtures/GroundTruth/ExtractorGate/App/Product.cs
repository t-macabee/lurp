using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace ExtractorGate.App;

public class Product
{
    [JsonPropertyName("product_name")]
    public string Name { get; set; } = "";
}

public class NoContract
{
    [DataMember]
    internal int IgnoredDataMember { get; set; }

    [JsonPropertyName("ignored_name")]
    internal int IgnoredStjName { get; set; }
}

[DataContract]
public class WithContract
{
    [DataMember]
    internal int IncludedDataMember { get; set; }
}

[JsonSerializable(typeof(Product))]
internal partial class GateJsonContext
{
}
