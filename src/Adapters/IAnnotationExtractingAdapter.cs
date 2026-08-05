using Microsoft.CodeAnalysis;
using Lurp.Shared;
using Lurp.Storage;

namespace Lurp.Adapters;

public interface IAnnotationExtractingAdapter
{
    List<AnnotationRecord> ExtractAnnotations(Compilation compilation, string snapshotId, EdgeLocationResolver locationResolver);
}
