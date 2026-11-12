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

// S01-F11: a new Dispose() that hides the base Dispose does not implement IDisposable.Dispose.
public sealed class ShapeHidingResource : ShapeBaseResource
{
    public new void Dispose()
    {
    }
}

// S01-F11: a public Dispose() next to the explicit IDisposable.Dispose is not the target.
public sealed class ShapeDualResource : IDisposable
{
    public void Dispose()
    {
    }

    void IDisposable.Dispose()
    {
    }
}
