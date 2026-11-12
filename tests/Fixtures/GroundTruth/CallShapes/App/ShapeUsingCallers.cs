namespace CallShapes.App;

// D10 (S01-F5): the target of each using is the member that implements IDisposable.Dispose
// (or IAsyncDisposable.DisposeAsync) on the static type of the resource.
public static class ShapeUsingCallers
{
    public static void FromInterfaceUsing(IShapeDisposable resource)
    {
        using (resource)
        {
        }
    }

    public static void FromExplicitUsing(ShapeExplicitResource resource)
    {
        using (resource)
        {
        }
    }

    public static void FromRefStructUsing()
    {
        using var resource = new ShapeRefResource();
    }

    public static async Task FromAwaitUsing(ShapeAsyncResource resource)
    {
        await using (resource)
        {
        }
    }

    public static void FromInheritedUsing(ShapeDerivedResource resource)
    {
        using (resource)
        {
        }
    }

    public static void FromNullableUsing(ShapeResource? resource)
    {
        using (resource)
        {
        }
    }

    public static void FromInterfaceConstrainedUsing<T>(T resource) where T : IDisposable
    {
        using (resource)
        {
        }
    }

    public static void FromDynamicUsing(dynamic resource)
    {
        using (resource)
        {
        }
    }

    public static void FromConstrainedUsing<T>(T resource) where T : ShapeBaseResource
    {
        using (resource)
        {
        }
    }

    public static void FromHidingUsing(ShapeHidingResource resource)
    {
        using (resource)
        {
        }
    }

    public static void FromDualUsing(ShapeDualResource resource)
    {
        using (resource)
        {
        }
    }
}
