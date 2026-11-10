namespace ExtractorGate.App;

public class BaseShape
{
    public void Run() { }
}

public class HidingShape : BaseShape
{
    public new void Run() { }
}

public static class ReflectionShapes
{
    public static string MemberName() => nameof(Calculator.Add);

    public static Type TypeOfShape() => typeof(Calculator);

    public static Type? ByName() => Type.GetType("ExtractorGate.App.Calculator");
}
