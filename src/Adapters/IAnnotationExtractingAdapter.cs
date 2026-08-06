using Lurp.Storage;

namespace Lurp.Adapters;

public interface IAnnotationExtractingAdapter
{
    List<AnnotationRecord> ExtractAnnotations(AdapterExtractionContext context);
}
