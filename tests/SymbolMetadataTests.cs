using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Tests;

public sealed class SymbolMetadataTests : InMemoryTestBase
{
    [Fact]
    public void Parse_NullInput_ReturnsNull()
    {
        Assert.Null(SymbolMetadata.Parse(null, "S:Null"));
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsNull()
    {
        Assert.Null(SymbolMetadata.Parse(string.Empty, "S:Empty"));
    }

    [Fact]
    public void Parse_ValidJson_ReturnsRootElement()
    {
        var metadata = SymbolMetadata.Parse("""{"accessibility":"Public"}""", "S:Valid");

        Assert.NotNull(metadata);
        Assert.Equal(JsonValueKind.Object, metadata!.Value.ValueKind);
        Assert.Equal("Public", metadata.Value.GetProperty(SymbolMetadataKeys.Accessibility).GetString());
    }

    [Fact]
    public void Parse_CorruptJson_ThrowsNamingSymbol()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SymbolMetadata.Parse("{not json", "S:Corrupt"));

        Assert.Contains("S:Corrupt", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Theory]
    [InlineData("M:N.Pub.A", "Public")]
    [InlineData("M:N.Pub.B", "Protected")]
    [InlineData("M:N.Pub.In.C", "Internal")]
    [InlineData("M:N.Pub.In.D", "Internal")]
    [InlineData("M:N.Pub.In.E", "ProtectedAndInternal")]
    [InlineData("M:N.Pub.Pr.F", "Private")]
    public async Task EffectiveAccessibility_IsTheMeetWithEveryContainingType(string docCommentId, string expectedEffective)
    {
        var extraction = await ExtractAsync(new Dictionary<string, string>
        {
            ["Test.cs"] = """
                          namespace N;

                          public class Pub
                          {
                              public void A() { }
                              protected void B() { }

                              internal class In
                              {
                                  public void C() { }
                                  protected internal void D() { }
                                  protected void E() { }
                              }

                              private class Pr
                              {
                                  public void F() { }
                              }
                          }
                          """
        });

        var decl = Assert.Single(extraction.Result.Declarations, d => d.SymbolId.DocCommentId == docCommentId);
        var metadata = SymbolMetadata.Parse(decl.MetadataJson, decl.SymbolId.ToString());
        Assert.NotNull(metadata);
        Assert.Equal(expectedEffective, metadata!.Value.GetProperty(SymbolMetadataKeys.EffectiveAccessibility).GetString());
    }

    [Fact]
    public async Task ImplementsExternalMember_CoversImplicitImplementationsAndExternalOverrides()
    {
        var extraction = await ExtractAsync(new Dictionary<string, string>
        {
            ["Test.cs"] = """
                          namespace N;

                          internal class Res : System.IDisposable
                          {
                              public void Dispose() { }
                          }

                          internal class Over
                          {
                              public override string ToString() => "";
                          }

                          internal class Svc : Microsoft.Extensions.Hosting.BackgroundService
                          {
                              protected override System.Threading.Tasks.Task ExecuteAsync(System.Threading.CancellationToken t) =>
                                  System.Threading.Tasks.Task.CompletedTask;
                          }

                          internal class Svc2 : Svc
                          {
                              protected override System.Threading.Tasks.Task ExecuteAsync(System.Threading.CancellationToken t) =>
                                  base.ExecuteAsync(t);
                          }

                          internal interface IOwn
                          {
                              void Run();
                          }

                          internal class Own : IOwn
                          {
                              public void Run() { }
                          }

                          internal class Base
                          {
                              public void Dispose() { }
                          }

                          internal class Derived : Base, System.IDisposable
                          {
                          }
                          """
        });

        bool HasExternalMemberFlag(string docCommentId)
        {
            var decl = Assert.Single(extraction.Result.Declarations, d => d.SymbolId.DocCommentId == docCommentId);
            var metadata = SymbolMetadata.Parse(decl.MetadataJson, decl.SymbolId.ToString());
            return SymbolMetadata.IsTrue(metadata, SymbolMetadataKeys.ImplementsExternalMember);
        }

        Assert.True(HasExternalMemberFlag("M:N.Res.Dispose"));
        Assert.True(HasExternalMemberFlag("M:N.Over.ToString"));
        Assert.True(HasExternalMemberFlag("M:N.Svc.ExecuteAsync(System.Threading.CancellationToken)"));
        Assert.True(HasExternalMemberFlag("M:N.Svc2.ExecuteAsync(System.Threading.CancellationToken)"));
        Assert.True(HasExternalMemberFlag("M:N.Base.Dispose"));
        Assert.False(HasExternalMemberFlag("M:N.Own.Run"));
    }
}
