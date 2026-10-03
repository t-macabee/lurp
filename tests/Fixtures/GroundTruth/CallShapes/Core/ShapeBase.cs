namespace CallShapes.Core;

public class ShapeBase
{
    protected ShapeBase(int value) => Value = value;

    protected ShapeBase()
    {
    }

    public int Value { get; }
}

public sealed class DerivedShape : ShapeBase
{
    public DerivedShape() : base(CoreApi.BaseConstructorArgument())
    {
    }
}

public class PrimaryShapeBase(int value)
{
    public int Value { get; } = value;
}

public sealed class PrimaryShape(int value) : PrimaryShapeBase(CoreApi.PrimaryConstructorBaseArgument())
{
    public int Doubled => value * 2;
}
