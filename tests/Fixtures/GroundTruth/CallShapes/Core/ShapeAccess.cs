namespace CallShapes.Core;

// D11 (S01-F8): each field is used through one access shape only.
public sealed class ShapeCounter
{
    public int Compound;

    public string? Coalesced;

    public int TupleLeft;

    public int TupleRight;

    public int ByRef;

    public int Aliased;
}
