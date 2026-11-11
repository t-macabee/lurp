namespace CallShapes.Core;

// D10 (S01-F5): one resource type for each using-target branch that the C# rules define.
public interface IShapeDisposable : IDisposable
{
}

public sealed class ShapeExplicitResource : IDisposable
{
    void IDisposable.Dispose()
    {
    }
}

public ref struct ShapeRefResource
{
    public void Dispose()
    {
    }
}

public sealed class ShapeAsyncResource : IAsyncDisposable
{
    public ValueTask DisposeAsync() => default;
}

public class ShapeBaseResource : IDisposable
{
    public virtual void Dispose()
    {
    }
}

public sealed class ShapeDerivedResource : ShapeBaseResource
{
}
