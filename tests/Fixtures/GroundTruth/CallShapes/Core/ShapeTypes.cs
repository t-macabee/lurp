using System.Runtime.CompilerServices;

namespace CallShapes.Core;

public sealed class ShapePublisher
{
    public event Action? Changed;

    public void Raise() => Changed?.Invoke();
}

public sealed class ShapeNumber
{
    public int Value { get; init; }

    public static ShapeNumber operator +(ShapeNumber left, ShapeNumber right) =>
        new() { Value = left.Value + right.Value };

    public static ShapeNumber operator -(ShapeNumber value) => new() { Value = -value.Value };

    public static implicit operator int(ShapeNumber value) => value.Value;

    public static explicit operator ShapeNumber(int value) => new() { Value = value };
}

public sealed class ShapeSequence
{
    public ShapeEnumerator GetEnumerator() => new();
}

public struct ShapeEnumerator
{
    public int Current { get; private set; }

    public bool MoveNext()
    {
        Current++;
        return Current <= 2;
    }
}

public sealed class ShapeAwaitable
{
    public ShapeAwaiter GetAwaiter() => new();
}

public sealed class ShapeAwaiter : INotifyCompletion
{
    public bool IsCompleted => true;

    public void OnCompleted(Action continuation) => continuation();

    public int GetResult() => 1;
}

public sealed class ShapeResource : IDisposable
{
    public void Dispose()
    {
    }
}

public sealed class ShapeBag : System.Collections.IEnumerable
{
    private readonly List<int> _items = [];

    public void Add(int value) => _items.Add(value);

    public System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
}

public sealed record ShapeRecord(int Id);

public sealed class ShapePair
{
    public void Deconstruct(out int left, out int right)
    {
        left = 1;
        right = 2;
    }
}

public sealed class ShapePattern
{
    public int Number { get; init; }
}

public sealed class ShapeMarkerAttribute : Attribute
{
    public ShapeMarkerAttribute(int value) => Value = value;

    public int Value { get; }
}

[InterpolatedStringHandler]
public readonly ref struct ShapeLogHandler
{
    public ShapeLogHandler(int literalLength, int formattedCount)
    {
    }

    public void AppendLiteral(string value)
    {
    }

    public void AppendFormatted<T>(T value)
    {
    }
}

public static class ShapeLog
{
    public static void Write(ShapeLogHandler handler)
    {
    }
}

public ref struct ShapeRefStruct
{
    public int Value;

    public int Read() => Value;
}

public static unsafe class ShapeUnsafe
{
    public static int Read(int* pointer) => *pointer;
}

public sealed class ShapeText
{
    public string Value { get; init; } = string.Empty;
}

public static class ShapeTextExtensions
{
    public static int ClassicWordCount(this ShapeText text) => text.Value.Split(' ').Length;

    extension(ShapeText text)
    {
        public int BlockWordCount() => text.Value.Split(' ').Length;
    }
}
