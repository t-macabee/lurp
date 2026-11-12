namespace CallShapes.Core;

// D13 (S01-F21): a recursive call is a Calls fact from the member to itself.
public class ShapeRecursion
{
    public static int Countdown(int n) => n <= 0 ? 0 : Countdown(n - 1);

    public virtual int Depth(int n) => n <= 0 ? 0 : Depth(n - 1);
}
