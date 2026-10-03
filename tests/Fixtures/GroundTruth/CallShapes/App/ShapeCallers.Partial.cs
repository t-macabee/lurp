namespace CallShapes.App;

public sealed partial class ShapeCallers
{
    public int PartialResult { get; private set; }

    public static int FromPartialMethod()
    {
        var shape = new ShapeCallers(0);
        shape.OnShapePartial(1);
        return shape.PartialResult;
    }

    public partial void OnShapePartial(int value) => PartialResult = CoreApi.PartialMethodTarget() + value;
}
