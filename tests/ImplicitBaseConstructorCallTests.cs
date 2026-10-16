namespace Lurp.Tests;

/// <summary>
///     I4-T1: a constructor without an initializer is modelled by Roslyn as an
///     invocation of the base type's default constructor. That constructor is
///     implicitly declared and never a snapshot symbol, so a Calls edge to it
///     would orphan out; the base relation is already the Inherits edge. Explicit
///     constructor initializers (: base(x), : this(x)) stay Calls facts.
/// </summary>
public sealed class ImplicitBaseConstructorCallTests : InMemoryTestBase
{
    private const string Doc = "Source.cs";

    private const string Source = """
                                  namespace N;

                                  public class Base
                                  {
                                      public int Value;
                                  }

                                  public class DerivedNoConstructor : Base
                                  {
                                  }

                                  public class DerivedExplicitParameterless : Base
                                  {
                                      public DerivedExplicitParameterless() { }
                                  }

                                  public class ExplicitBase
                                  {
                                      public ExplicitBase() { }
                                      public ExplicitBase(int value) { }
                                  }

                                  public class DerivedExplicitBase : ExplicitBase
                                  {
                                      public DerivedExplicitBase() : base() { }
                                  }

                                  public class DerivedBaseInitializer : ExplicitBase
                                  {
                                      public DerivedBaseInitializer() : base(1) { }
                                  }

                                  public class DerivedThisInitializer : ExplicitBase
                                  {
                                      public DerivedThisInitializer() : this(1) { }
                                      public DerivedThisInitializer(int value) { }
                                  }
                                  """;

    private static Dictionary<string, string> One(string source)
    {
        return new Dictionary<string, string> { [Doc] = source };
    }

    [Fact]
    public async Task ImplicitBaseConstructorCalls_AreNotExtracted()
    {
        var extraction = await ExtractAsync(One(Source));

        Assert.DoesNotContain(extraction.EdgesOf("Calls"),
            edge => edge.TargetSymbolId.StartsWith("M:N.Base.#ctor|", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExplicitConstructorInitializers_StayCalls()
    {
        var extraction = await ExtractAsync(One(Source));

        var calls = extraction.EdgesOf("Calls");

        // : base() to an explicitly declared parameterless base constructor.
        Assert.Contains(calls,
            edge => edge.SourceSymbolId.StartsWith("M:N.DerivedExplicitBase.#ctor|", StringComparison.Ordinal) &&
                    edge.TargetSymbolId.StartsWith("M:N.ExplicitBase.#ctor|", StringComparison.Ordinal));

        // : base(1) to an explicitly declared base constructor.
        Assert.Contains(calls,
            edge => edge.SourceSymbolId.StartsWith("M:N.DerivedBaseInitializer.#ctor|", StringComparison.Ordinal) &&
                    edge.TargetSymbolId.StartsWith("M:N.ExplicitBase.#ctor(System.Int32)|", StringComparison.Ordinal));

        // : this(1) to an explicitly declared constructor of the same type.
        Assert.Contains(calls,
            edge => edge.SourceSymbolId.StartsWith("M:N.DerivedThisInitializer.#ctor|", StringComparison.Ordinal) &&
                    edge.TargetSymbolId.StartsWith("M:N.DerivedThisInitializer.#ctor(System.Int32)|", StringComparison.Ordinal));
    }
}
