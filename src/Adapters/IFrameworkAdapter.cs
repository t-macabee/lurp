using Lurp.Storage;

namespace Lurp.Adapters;

public interface IFrameworkAdapter
{
    string Name { get; }
    string Version { get; }
    List<EdgeRecord> Extract(AdapterExtractionContext context);
}
