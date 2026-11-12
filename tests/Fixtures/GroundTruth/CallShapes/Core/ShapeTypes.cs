using System.Runtime.CompilerServices;

namespace CallShapes.Core;

public sealed class ShapePublisher
{
    public event Action? Changed;

    public event System.Action? CustomChanged { add { } remove { } }

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

// S01-F9: user-defined conditional logical operators (C# 12.15.3).
public sealed class ShapeTriState
{
    public static bool operator true(ShapeTriState value) => true;

    public static bool operator false(ShapeTriState value) => false;

    public static ShapeTriState operator &(ShapeTriState left, ShapeTriState right) => left;

    public static ShapeTriState operator |(ShapeTriState left, ShapeTriState right) => right;
}

// D12 (S01-F27): a lambda depends on its parameter and return types, written or inferred.
public sealed class ShapeLambdaArg
{
    public int Value { get; init; }
}

public sealed class ShapeLambdaResult
{
}

public static class ShapeLambdaHost
{
    public static int Run(Func<ShapeLambdaArg, int> body) => body(new ShapeLambdaArg());

    public static ShapeLambdaResult Make(Func<ShapeLambdaResult> factory) => factory();
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

public struct ShapeCell
{
    public int Value;
}

public static unsafe class ShapeUnsafe
{
    public static int Read(int* pointer) => *pointer;

    public static delegate*<ShapeCell, ShapeText> CellToText;

    public static int ReadCell(ShapeCell* cell) => cell->Value;

    public static int CountCells()
    {
        delegate*<ShapeCell, void> sink = null;
        return sink == null ? 0 : 1;
    }
}

public sealed class ShapeText
{
    public string Value { get; init; } = string.Empty;

    public string Note { get; set; } = string.Empty;
}

public sealed class ShapeBox<T>
{
    public T? Item { get; set; }
}

public static class ShapeTextExtensions
{
    public static int ClassicWordCount(this ShapeText text) => text.Value.Split(' ').Length;

    extension(ShapeText text)
    {
        public int BlockWordCount() => text.Value.Split(' ').Length;

        public int BlockLength => text.Value.Length;

        public string BlockTag
        {
            get => text.Note;
            set => text.Note = value;
        }
    }

    extension<T>(ShapeBox<T> box)
    {
        public T? BlockUnbox() => box.Item;

        public bool BlockHasItem => box.Item is not null;
    }
}
