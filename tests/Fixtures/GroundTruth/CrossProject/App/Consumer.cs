using CrossProject.Lib;

namespace CrossProject.App;

public static class Consumer
{
    public static int RunPublic() => PublicApi.SharedTarget();

    public static int RunConditional() => PublicApi.OnlyNet10();

    public static int RunInternal() => InternalApi.InternalTarget();

    public static int RunGeneric() => new ShapeRepo<int>().Get(7);

    public static int RunNested() => new ShapeRepo<int>.Nested<string>().Echo("shape").Length;

    public static int RunPartial() => new PartialShape().FromPartOne() + new PartialShape().FromPartTwo();
}
