namespace CallShapes.App;

public static class ShapeImplicitCallers
{
    public static async Task<int> FromExtensionAwait() => await new ShapeExtAwaitable();

    public static int FromExtensionForeach()
    {
        var sum = 0;
        foreach (var item in new ShapeExtSequence())
            sum += item;
        return sum;
    }

    public static int FromExtensionDeconstruct()
    {
        var (left, right) = new ShapeExtPair();
        return left + right;
    }

    public static int FromNestedDeconstruction()
    {
        var (a, (b, c)) = new ShapeOuterPair();
        return a + b + c;
    }

    public static bool FromPositionalPattern(ShapePositional value) => value is (var left, var right) && left < right;

    public static int FromForeachDeconstruction(ShapeForeachItem[] items)
    {
        var sum = 0;
        foreach (var (key, value) in items)
            sum += key + value;
        return sum;
    }

    public static bool FromListPattern(ShapeListLike list) => list is [var first, .. var rest] && first == 0 && rest is not null;

    public static int FromImplicitIndex(ShapeIndexLike items) => items[^1];

    public static ShapeIndexLike FromImplicitRange(ShapeIndexLike items) => items[1..];

    public static void FromImplicitIndexWrite(ShapeIndexWritable items) => items[^1] = 4;

    public static ShapeBuilt FromCollectionBuilder() => [1, 2];

    public static ShapeAddOnly FromCollectionAdd() => [1, 2];

    public static int FromBlockProperty(ShapeText text) => text.BlockLength;

    public static string FromBlockPropertyReadWrite(ShapeText text)
    {
        text.BlockTag = "a";
        text.BlockTag += "b";
        return text.BlockTag;
    }

    public static int FromGenericBlockMethod(ShapeBox<int> box) => box.BlockUnbox();

    public static bool FromGenericBlockProperty(ShapeBox<int> box) => box.BlockHasItem;
}
