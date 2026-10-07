using System.Runtime.CompilerServices;

namespace CallShapes.Core;

// D3 proof (B1 step 5): pattern members that only Roslyn's binding APIs resolve.
public sealed class ShapeExtAwaitable
{
}

public sealed class ShapeExtAwaiter : INotifyCompletion
{
    public bool IsCompleted => true;

    public void OnCompleted(Action continuation) => continuation();

    public int GetResult() => 2;
}

public sealed class ShapeExtSequence
{
}

public sealed class ShapeExtEnumerator
{
    public int Current { get; private set; }

    public bool MoveNext()
    {
        Current++;
        return Current <= 1;
    }
}

public sealed class ShapeExtPair
{
}

public sealed class ShapeOuterPair
{
    public void Deconstruct(out int left, out ShapeInnerPair right)
    {
        left = 1;
        right = new ShapeInnerPair();
    }
}

public sealed class ShapeInnerPair
{
    public void Deconstruct(out int left, out int right)
    {
        left = 2;
        right = 3;
    }
}

public static class ShapeImplicitExtensions
{
    public static ShapeExtAwaiter GetAwaiter(this ShapeExtAwaitable awaitable) => new();

    public static ShapeExtEnumerator GetEnumerator(this ShapeExtSequence sequence) => new();

    public static void Deconstruct(this ShapeExtPair pair, out int left, out int right)
    {
        left = 4;
        right = 5;
    }
}

// D3 proof (B19): implicit calls that both walks omit. Each target is used only through its shape.
public sealed class ShapePositional
{
    public void Deconstruct(out int left, out int right)
    {
        left = 6;
        right = 7;
    }
}

public sealed class ShapeForeachItem
{
    public void Deconstruct(out int key, out int value)
    {
        key = 8;
        value = 9;
    }
}

public sealed class ShapeListLike
{
    public int Count => 2;

    public int this[int index] => index;

    public ShapeListLike Slice(int start, int length) => this;
}

public sealed class ShapeIndexLike
{
    public int Count => 3;

    public int this[int index] => index;

    public ShapeIndexLike Slice(int start, int length) => this;
}

public sealed class ShapeIndexWritable
{
    private int _last;

    public int Count => 1;

    public int this[int index]
    {
        get => _last;
        set => _last = value;
    }
}

[CollectionBuilder(typeof(ShapeBuiltBuilder), "Create")]
public sealed class ShapeBuilt : IEnumerable<int>
{
    public IEnumerator<int> GetEnumerator() => Enumerable.Empty<int>().GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class ShapeBuiltBuilder
{
    public static ShapeBuilt Create(ReadOnlySpan<int> values) => new();
}

public sealed class ShapeAddOnly : System.Collections.IEnumerable
{
    public void Add(int value)
    {
    }

    public System.Collections.IEnumerator GetEnumerator() => Array.Empty<int>().GetEnumerator();
}

// D3 proof (B20): targets used only from nested types in CallShapes.App.
public sealed class ShapeNestedTarget
{
    public int Counter { get; set; }
}

public sealed class ShapeNestedCreated
{
    public int Value => 1;
}

public sealed class ShapeNestedException : Exception
{
}
