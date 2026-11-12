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

    // D20: the getter owns the expression body of a property.
    public static Type ShapeType => typeof(Calculator);
}

// S01-F17: a member hides a member that its type inherits through a base type that does not declare it.
public class PassThroughShape : BaseShape { }

public class DeepHidingShape : PassThroughShape
{
    public new void Run() { }
}
