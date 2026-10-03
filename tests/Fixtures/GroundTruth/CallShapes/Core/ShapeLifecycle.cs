namespace CallShapes.Core;

public sealed class ShapeLifecycle
{
    private int _value;

    static ShapeLifecycle()
    {
        CoreApi.StaticConstructorTarget();
    }

    ~ShapeLifecycle()
    {
        CoreApi.FinalizerTarget();
    }

    public int InitAccessed
    {
        get => _value;
        init => _value = CoreApi.InitAccessorTarget();
    }

    public int BlockAccessed
    {
        get { return CoreApi.AccessorBodyTarget(); }
    }
}
