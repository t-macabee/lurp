// B33 (step 4a): roots outside method bodies.

namespace CallShapes.Core;

public static class ShapeRoots
{
    public const int DefaultSeed = 3;

    public const int AttributeSeed = 19;

    public const int EnumSeed = 5;

    public const int DelegateSeed = 7;

    public static int WithDefault(int v = DefaultSeed) => v;

    public static event System.Action? Seeded = SeedHandler;

    public static void SeedHandler()
    {
    }

    [ShapeMarker(AttributeSeed)]
    public static void Marked()
    {
    }
}

public class ShapeSeedIndex
{
    public int this[int i, int j = ShapeRoots.DefaultSeed] => i + j;
}

public enum ShapeSeeded
{
    First = ShapeRoots.EnumSeed
}

public delegate int ShapeSeedDelegate(int v = ShapeRoots.DelegateSeed);
