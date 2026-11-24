using Lurp.Storage;

namespace Lurp.Tests;

public sealed class DeadCandidatePhase4Tests : IntegrationTestBase
{
    [Fact]
    public async Task RuntimeConventionsProtectMembersOfNonPublicTypes()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["App.cs"] = """
                         namespace Sample;
                         internal class Context { public int Set { get; } }
                         internal class Entity { public Entity() {} internal int Value { get; set; } }
                         internal class Configuration {}
                         internal class UnmodeledHandler { public UnmodeledHandler() {} }
                         internal class Serializer
                         {
                             public Serializer() {}
                             private void OnRead() {}
                             private void Unused() {}
                         }
                         internal class Options
                         {
                             public Options() {}
                             public int Value { get; set; }
                             public int Field;
                             public void Unused() {}
                         }
                         internal class Service { public Service() {} }
                         internal class Handler { public Handler() {} public void Handle() {} }
                         internal class Controller { public Controller() {} public void Action() {} }
                         """
        });

        var snapshot = await RunFullIndexAsync(DbPath);
        using var store = OpenStore(DbPath);
        string Id(string name) => ResolveSymbolId(snapshot, $"global::Sample.{name}");
        store.SaveAnnotations(snapshot,
        [
            new(Id("UnmodeledHandler"), "unmodeled_mediatr_pattern", "fixture", "src/App/App.cs"),
            new(Id("Serializer.OnRead"), "serialization_contract", "included", "src/App/App.cs"),
            new(SymbolId.Compose("M:Sample.Serializer.#ctor", SymbolId.Parse(Id("Serializer")).AssemblyIdentity),
                "serialization_contract", "included", "src/App/App.cs"),
            new(Id("Options"), "options_binding", "configured", "src/App/App.cs")
        ]);
        store.SaveEdges(snapshot,
        [
            Edge(Id("Context.Set"), Id("Entity"), EdgeKind.MapsTo),
            Edge(Id("Configuration"), Id("Entity"), EdgeKind.MapsTo),
            Edge(Id("Context"), Id("Service"), EdgeKind.Registers),
            Edge(Id("Context"), Id("Handler.Handle"), EdgeKind.Handles),
            Edge("route://get/sample", Id("Controller.Action"), EdgeKind.RoutesTo)
        ]);

        var page = store.GetDeadCandidatesPage(snapshot, null, null, null, false, false, false, 200, null);
        Check(DeadCandidateReason.EfConvention, "P:Context.Set", "T:Configuration", "M:Entity.#ctor",
            "P:Entity.Value", "M:Entity.get_Value", "M:Entity.set_Value(System.Int32)");
        Check(DeadCandidateReason.SerializationConvention, "M:Serializer.#ctor", "M:Serializer.OnRead");
        Check(DeadCandidateReason.FrameworkConvention, "T:UnmodeledHandler", "M:UnmodeledHandler.#ctor",
            "M:Options.#ctor", "P:Options.Value", "M:Options.get_Value", "M:Options.set_Value(System.Int32)",
            "F:Options.Field", "M:Service.#ctor", "M:Handler.#ctor", "M:Controller.#ctor");
        Check(DeadCandidateReason.NoIncomingLiveEdges, "M:Options.Unused", "M:Serializer.Unused");

        void Check(string reason, params string[] expected)
        {
            foreach (var name in expected)
            {
                var docId = $"{name[..2]}Sample.{name[2..]}";
                var candidate = Assert.Single(page.Candidates, c => c.SymbolId.Split('|')[0] == docId);
                Assert.Equal(reason, candidate.Reason);
                Assert.Equal(reason == DeadCandidateReason.NoIncomingLiveEdges
                    ? DeadCandidateStatus.ProvedDead : DeadCandidateStatus.UncertainDead, candidate.Status);
            }
        }

        static EdgeRecord Edge(string source, string target, EdgeKind kind) => new()
        {
            SourceSymbolId = source,
            TargetSymbolId = target,
            Kind = kind.ToString(),
            Provenance = Provenance.FrameworkDerived,
            ExtractorVersion = "fixture"
        };
    }

    [Fact]
    public async Task LinkedDeclarationsShareLivenessAndPreferTheOwningProject()
    {
        const string path = "src/ZOwner/Shared.cs";
        CreateProject("ZOwner", new Dictionary<string, string>
        {
            ["Shared.cs"] = """
                            namespace Shared;
                            internal class Helper
                            {
                                internal static void Used() {}
                                internal static void Recursive() => Recursive();
                                internal static int Value { get; set; }
                            }
                            """
        }, msbuildProperties: new Dictionary<string, string> { ["AssemblyName"] = "ZOwner.Assembly" });
        CreateProject("AConsumer", new Dictionary<string, string>
        {
            ["Consumer.cs"] = """
                              namespace Shared;
                              internal class Consumer
                              {
                                  internal void Use() => Helper.Used();
                                  internal int Read() => Helper.Value;
                              }
                              """
        });
        var csproj = Path.Combine(TestDir, "src", "AConsumer", "AConsumer.csproj");
        File.WriteAllText(csproj, File.ReadAllText(csproj).Replace("</Project>",
            """<ItemGroup><Compile Include="../ZOwner/Shared.cs" Link="Shared.cs" /></ItemGroup></Project>"""));

        var snapshot = await RunFullIndexAsync(DbPath);
        using var store = OpenStore(DbPath);
        var owner = store.GetDeadCandidatesPage(snapshot, "ZOwner.Assembly", null, "Method", false, false, false, 200, null);
        var ownerDeadMethods = owner.Candidates.Where(c => c.Status == DeadCandidateStatus.ProvedDead)
            .Select(c => SymbolId.DocCommentIdOf(c.SymbolId)).ToArray();
        Assert.Equal(["M:Shared.Helper.Recursive", "M:Shared.Helper.set_Value(System.Int32)"], ownerDeadMethods);
        Assert.DoesNotContain(owner.Candidates, c => SymbolId.DocCommentIdOf(c.SymbolId) is
            "M:Shared.Helper.Used" or "M:Shared.Helper.get_Value");

        var ownerTypes = store.GetDeadCandidatesPage(snapshot, "ZOwner.Assembly", null, "Type", false, false, false, 200, null);
        Assert.Empty(ownerTypes.Candidates);
        Assert.Contains("|ZOwner.Assembly,", store.NavigateToLocation(path, 4, snapshot)!.SymbolId);

        OutlineCursor? cursor = null;
        var entries = new List<string>();
        do
        {
            var page = store.GetDeclarationsOutline(path, snapshot, false, 1, cursor)!;
            Assert.Equal(6, page.TotalCount);
            entries.Add(Assert.Single(page.Items).SymbolId);
            cursor = page.NextCursor is { } next
                ? Assert.IsType<OutlineCursor>(OutlineCursor.TryDecode(next))
                : null;
        } while (cursor != null);
        Assert.Equal(6, entries.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(6, entries.Count);
        Assert.All(entries, id => Assert.Contains("|ZOwner.Assembly,", id));
    }
}
