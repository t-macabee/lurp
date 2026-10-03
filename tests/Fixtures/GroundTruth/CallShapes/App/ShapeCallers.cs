namespace CallShapes.App;

public sealed partial class ShapeCallers
{
    public static readonly int FieldInitializerValue = CoreApi.FieldInitializerTarget();

    public static int PropertyInitializerValue { get; } = CoreApi.PropertyInitializerTarget();

    public ShapeCallers() : this(CoreApi.ConstructorInitializerTarget())
    {
    }

    public ShapeCallers(int value) => ConstructorValue = value;

    public int ConstructorValue { get; }

    public static int ExpressionBodiedProperty => CoreApi.ExpressionBodiedPropertyTarget();

    public partial void OnShapePartial(int value);

    public static int FromMethodGroup(int[] values) => values.Select(CoreApi.MethodGroupTarget).Sum();

    public static void FromEventSubscription(ShapePublisher publisher) =>
        publisher.Changed += CoreApi.EventHandlerTarget;

    public static int FromQueryLambda(int[] values) =>
        (from value in values
         where value > 0
         select CoreApi.QueryLambdaTarget(value)).Sum();

    public static int FromLocalFunction()
    {
        return Local();

        static int Local() => CoreApi.LocalFunctionTarget();
    }

    public static int FromOperators(ShapeNumber left, ShapeNumber right)
    {
        var sum = left;
        sum += right;
        return sum.Value;
    }

    public static ShapeNumber FromUnaryOperator(ShapeNumber value) => -value;

    public static int FromImplicitConversion()
    {
        int value = new ShapeNumber { Value = 1 };
        return value;
    }

    public static ShapeNumber FromExplicitConversion() => (ShapeNumber)1;

    public static int FromForeach(ShapeSequence sequence)
    {
        var sum = 0;
        foreach (var item in sequence)
        {
            sum += item;
        }

        return sum;
    }

    public static void FromUsing()
    {
        using var resource = new ShapeResource();
    }

    public static async Task<int> FromAwait()
    {
        return await new ShapeAwaitable();
    }

    public static int FromDeconstruction()
    {
        var (left, right) = new ShapePair();
        return left + right;
    }

    public static ShapeBag FromCollectionInitializer() => new ShapeBag { 1, 2 };

    public static int[] FromCollectionExpression() => [1, 2, 3];

    public static ShapeRecord FromWith(ShapeRecord original) => original with { Id = CoreApi.RecordTarget() };

    [ShapeMarker(19)]
    public static void FromAttribute()
    {
    }

    public static int FromOmittedArgument() => CoreApi.OmittedArgumentTarget();

    public static bool FromPropertyPattern(ShapePattern pattern) => pattern is { Number: > 0 };

    public static unsafe int FromUnsafe()
    {
        var value = 1;
        return ShapeUnsafe.Read(&value);
    }

    public static int FromRefStruct()
    {
        var holder = new ShapeRefStruct { Value = 1 };
        return holder.Read();
    }

    public static int FromDynamic()
    {
        dynamic value = CoreApi.DynamicTarget();
        return value.ToString().Length;
    }

    public static async IAsyncEnumerable<int> FromAsyncIterator()
    {
        await Task.Yield();
        yield return CoreApi.AsyncIteratorTarget();
    }

    public static void FromInterpolatedHandler(int value) =>
        ShapeLog.Write($"value {CoreApi.InterpolatedHandlerTarget()} and {value}");

    public static int FromClassicExtension(ShapeText text) => text.ClassicWordCount();

    public static int FromBlockExtension(ShapeText text) => text.BlockWordCount();

    public static int FromContract(IShapeContract contract) => contract.ContractValue();

    public static int FromDefaultInterface(IShapeDefault value) => value.DefaultValue();

    public static ShapeFactory FromStaticAbstract() => ShapeFactoryUser.CreateShape<ShapeFactory>();
}
