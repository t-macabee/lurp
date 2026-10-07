namespace CallShapes.Core;

public static class CoreApi
{
    public static int TopLevelTarget() => 1;

    public static int FieldInitializerTarget() => 2;

    public static int PropertyInitializerTarget() => 3;

    public static int ExpressionBodiedPropertyTarget() => 4;

    public static int ConstructorInitializerTarget() => 5;

    public static int BaseConstructorArgument() => 6;

    public static int MethodGroupTarget(int value) => value;

    public static void EventHandlerTarget()
    {
    }

    public static int QueryLambdaTarget(int value) => value;

    public static int LocalFunctionTarget() => 7;

    public static int PartialMethodTarget() => 8;

    public static int AsyncIteratorTarget() => 9;

    public static int InterpolatedHandlerTarget() => 10;

    public static int StaticConstructorTarget() => 11;

    public static int FinalizerTarget() => 12;

    public static int InitAccessorTarget() => 13;

    public static int AccessorBodyTarget() => 14;

    public static int ExplicitInterfaceTarget() => 15;

    public static int DefaultInterfaceTarget() => 16;

    public static int DynamicTarget() => 17;

    public static int RecordTarget() => 18;

    public static int PrimaryConstructorBaseArgument() => 20;

    public static int OmittedArgumentTarget(int value = 21) => value;

    public static string MinimalApiTarget() => "shape";

    public static int OptionsTarget() => 22;

    public static int NestedFieldInitializerTarget() => 23;

    public static int NestedConstructorInitializerTarget() => 24;

    public static int NestedCallTarget() => 25;

    public static int NestedDeepTarget() => 26;

    public static int NestedOverrideTarget() => 27;
}
