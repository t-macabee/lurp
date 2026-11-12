namespace CallShapes.Core;

// S01-F12: an event accessor body is a root, and the accessor owns it.
public sealed class ShapeEventHost
{
    private Action? _handlers;

    public event Action? Tracked
    {
        add
        {
            _handlers += value;
            ShapeEventLog.OnAdd();
        }
        remove
        {
            _handlers -= value;
            ShapeEventLog.OnRemove();
        }
    }
}

public static class ShapeEventLog
{
    public static void OnAdd()
    {
    }

    public static void OnRemove()
    {
    }
}
