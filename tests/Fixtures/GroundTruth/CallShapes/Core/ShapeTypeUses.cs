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
