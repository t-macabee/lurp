using Lurp.Storage;

namespace Lurp.Tests;

public sealed class FqnFormsTests
{
    [Theory]
    [InlineData("global::Ns.Repo<T>", "global::Ns.Repo")]
    [InlineData("global::Ns.Repo<T>.Add", "global::Ns.Repo.Add")]
    [InlineData("global::Ns.Map<TKey, TValue>.Get<TResult>", "global::Ns.Map.Get")]
    [InlineData("global::Ns.Outer<T>.Inner<U>", "global::Ns.Outer.Inner")]
    [InlineData("global::Ns.Plain", "global::Ns.Plain")]
    [InlineData("global::Ns.Bad<T", "global::Ns.Bad<T")]
    [InlineData("global::Ns.Bad>T", "global::Ns.Bad>T")]
    [InlineData("global::Ns.A<B<C>>.M", "global::Ns.A.M")]
    public void RemoveTypeParameterLists_MatchesD181Rule(string input, string expected)
    {
        Assert.Equal(expected, FqnForms.RemoveTypeParameterLists(input));
    }
}
