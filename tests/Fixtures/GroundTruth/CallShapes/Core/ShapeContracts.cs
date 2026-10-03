namespace CallShapes.Core;

public interface IShapeContract
{
    int ContractValue();
}

public sealed class ShapeContractImpl : IShapeContract
{
    int IShapeContract.ContractValue() => CoreApi.ExplicitInterfaceTarget();
}

public interface IShapeDefault
{
    int DefaultValue() => CoreApi.DefaultInterfaceTarget();
}

public sealed class ShapeDefaultImpl : IShapeDefault
{
}

public interface IShapeFactory<TSelf> where TSelf : IShapeFactory<TSelf>
{
    static abstract TSelf CreateShape();
}

public sealed class ShapeFactory : IShapeFactory<ShapeFactory>
{
    public static ShapeFactory CreateShape() => new();
}

public static class ShapeFactoryUser
{
    public static T CreateShape<T>() where T : IShapeFactory<T> => T.CreateShape();
}
