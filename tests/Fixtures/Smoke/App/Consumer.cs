namespace Smoke.App;

public sealed class Consumer
{
    public string Run()
    {
        return Lib.Greet("smoke");
    }
}
