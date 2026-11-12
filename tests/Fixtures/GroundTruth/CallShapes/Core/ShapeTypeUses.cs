// B30 (step 4a): types used only in bodies or inside signatures.

namespace CallShapes.Core;

public static class ShapeTypeUses
{
    public static bool FromIs(object o) => o is ShapeOnlyIsChecked;

    public static int FromCatch()
    {
        try
        {
            return 0;
        }
        catch (ShapeOnlyCaught)
        {
            return 1;
        }
    }

    public static int FromGenericArg() => System.Array.Empty<ShapeOnlyGenericArg>().Length;

    public static ShapeOnlyArrayElement[]? FromArrayReturn() => null;

    public static System.Collections.Generic.List<ShapeOnlyListArg>? FromListReturn() => null;

    public static int FromNestedGeneric(ShapeOuter<ShapeOuterArg>.Inner value) => value is null ? 0 : 1;
}

public sealed class ShapeOnlyIsChecked
{
}

public sealed class ShapeOnlyCaught : System.Exception
{
}

public sealed class ShapeOnlyGenericArg
{
}

public sealed class ShapeOnlyArrayElement
{
}

public sealed class ShapeOnlyListArg
{
}

// S01-F13 (c): a nested type of a constructed generic type names the type arguments of its
// containing type.
public sealed class ShapeOuter<T>
{
    public sealed class Inner
    {
    }
}

public sealed class ShapeOuterArg
{
}
