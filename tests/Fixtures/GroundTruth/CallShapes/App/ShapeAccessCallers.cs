namespace CallShapes.App;

// D11 (S01-F8): one member for each access shape of mapping rule 4 that reads and writes,
// or writes only, and that no other member covers.
public static class ShapeAccessCallers
{
    public static void FromCompoundAssignment(ShapeCounter counter) => counter.Compound += 1;

    public static void FromCoalesceAssignment(ShapeCounter counter) => counter.Coalesced ??= "x";

    public static void FromTupleTarget(ShapeCounter counter) => (counter.TupleLeft, counter.TupleRight) = (1, 2);

    public static void FromRefArgument(ShapeCounter counter) => Bump(ref counter.ByRef);

    public static void FromRefLocal(ShapeCounter counter)
    {
        ref var alias = ref counter.Aliased;
        alias = 5;
    }

    private static void Bump(ref int value) => value++;
}
