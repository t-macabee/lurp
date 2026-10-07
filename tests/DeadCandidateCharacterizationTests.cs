using Lurp.Storage;
using Microsoft.Build.Locator;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

/// <summary>
///     Phase 4 characterization scaffold for the dead-candidate detector
///     (<c>lurp-phases-report.md</c> Phase 2 design, Phase 3 implementation at
///     commit <c>4cb8ae4</c>, Phase 4 verification). Pins the suppression-ladder
///     contract of <see cref="DeadCandidateStore.GetDeadCandidatesPage" />: one
///     <c>reason</c>/<c>status</c> per candidate, batched incoming-edge queries,
///     and the <c>include_public</c>/<c>include_generated</c> toggles.
///     Uses <see cref="IntegrationTestBase" /> (real MSBuild indexing) for natural
///     fixtures, plus direct <c>store.SaveEdges</c> injection to isolate individual
///     weak-provenance ladder branches that no single natural C# construct can
///     produce in isolation from the others (see
///     <see cref="PossibleDispatch_InheritedOnly_Uncertain" />).
/// </summary>
public sealed class DeadCandidateCharacterizationTests : IntegrationTestBase
{
    private static EdgeRecord MakeEdge(string source, string target, string kind, string provenance)
    {
        return new EdgeRecord
        {
            SourceSymbolId = source,
            TargetSymbolId = target,
            Kind = kind,
            Provenance = provenance,
            ExtractorVersion = "0.0.0-injected",
            SourceDocumentPath = "src/TestProject/Injected.cs",
            SourceStartLine = 1,
            SourceStartColumn = 1,
            SourceEndLine = 1,
            SourceEndColumn = 1
        };
    }

    [SkippableFact]
    public async Task ProvedDead_InternalHelper_NoLiveIncoming()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Util.cs"] = """
                          namespace TestProject;

                          internal static class Util
                          {
                              internal static void Helper() { }
                          }
                          """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var helperId = ResolveSymbolId(snapshotId, "global::TestProject.Util.Helper");

        using var store = OpenStore(DbPath);
        try
        {
            var page = store.GetDeadCandidatesPage(snapshotId, null, null, null, false, false, false, 200, null);

            var entry = Assert.Single(page.Candidates, c => c.SymbolId == helperId);
            Assert.Equal(DeadCandidateStatus.ProvedDead, entry.Status);
            Assert.Equal(DeadCandidateReason.NoIncomingLiveEdges, entry.Reason);
            Assert.Empty(entry.Uncertainties);
            Assert.True(page.DeadCount >= 1);
            Assert.True(page.CandidateCount >= 1);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task CorruptMetadataJson_ThrowsNamingSymbol()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Util.cs"] = """
                          namespace TestProject;

                          internal static class Util
                          {
                              internal static void Helper() { }
                          }
                          """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var helperId = ResolveSymbolId(snapshotId, "global::TestProject.Util.Helper");

        using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE snapshot_symbols
                SET metadata_json = @json
                WHERE snapshot_id = @snapshotId AND symbol_id = @symbolId;
                """;
            command.Parameters.AddWithValue("@json", "{not valid json");
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            command.Parameters.AddWithValue("@symbolId", helperId);
            command.ExecuteNonQuery();
        }

        using var store = OpenStore(DbPath);
        try
        {
            // Lurp writes metadata_json itself; a row that does not parse is an extractor
            // or storage bug, so the read fails loudly and names the offending symbol.
            var ex = Assert.Throws<InvalidOperationException>(() =>
                store.GetDeadCandidatesPage(snapshotId, null, null, null, false, false, false, 200, null));
            Assert.Contains(helperId, ex.Message);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task NullSpanColumns_NoLocationWithLineZero_KeepsDocumentPathAndMatchesDocumentFilter()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Util.cs"] = """
                          namespace TestProject;

                          internal static class Util
                          {
                              internal static void Helper() { }
                          }
                          """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var helperId = ResolveSymbolId(snapshotId, "global::TestProject.Util.Helper");
        const string helperFile = "src/TestProject/Util.cs";

        using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE declarations
                SET full_start = NULL, full_end = NULL
                WHERE symbol_id = @symbolId
                  AND document_version_id IN (
                      SELECT document_version_id FROM snapshot_documents WHERE snapshot_id = @snapshotId);
                """;
            command.Parameters.AddWithValue("@symbolId", helperId);
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            command.ExecuteNonQuery();
        }

        using var store = OpenStore(DbPath);
        try
        {
            var page = store.GetDeadCandidatesPage(snapshotId, null, null, null, false, false, false, 200, null);

            var entry = Assert.Single(page.Candidates, c => c.SymbolId == helperId);
            // A declaration without span data has no line mapping: it emits no location
            // (never a placeholder with line 0) but keeps its document membership.
            Assert.Empty(entry.Locations);
            Assert.DoesNotContain(entry.Locations, l => l.StartLine == 0 || l.EndLine == 0);
            Assert.Equal(helperFile, entry.DocumentPath);

            var filtered = store.GetDeadCandidatesPage(snapshotId, null, helperFile, null, false, false, false, 200, null);
            Assert.Contains(filtered.Candidates, c => c.SymbolId == helperId);
        }
        finally
        {
            store.Close();
        }
    }

    /// <summary>
    ///     Characterization for the entry-point suppression branch. Before this branch existed,
    ///     the compiler-synthesized top-level-statements entry point method landed in the
    ///     terminal no-incoming-edges branch and read as <c>proved_dead</c> — reproduced live
    ///     against a real solution during the eNoteV2 capability battery
    ///     (2026-08-21) and confirmed here by dedicated repro: nothing in-repo ever calls the
    ///     entry point (only the runtime launcher does), so "no incoming live edges" is
    ///     definitional for this symbol, not evidence of dead code. Program.cs holds only the
    ///     top-level statements — Greeter lives in a separate file — so the document filter
    ///     below isolates exactly the synthesized entry-point method as the sole Method-kind
    ///     candidate declared there.
    /// </summary>
    [SkippableFact]
    public async Task ProcessEntryPoint_TopLevelStatements_IsUncertainNotProvedDead()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("EntryPointProbe",
            new Dictionary<string, string>
            {
                ["Program.cs"] = """
                                  var svc = new EntryPointProbe.Greeter();
                                  svc.Greet();
                                  """,
                ["Greeter.cs"] = """
                                 namespace EntryPointProbe;

                                 public class Greeter
                                 {
                                     public void Greet() => System.Console.WriteLine("hi");
                                 }
                                 """
            },
            msbuildProperties: new Dictionary<string, string> { ["OutputType"] = "Exe" });

        var snapshotId = await RunFullIndexAsync(DbPath);

        using var store = OpenStore(DbPath);
        try
        {
            var page = store.GetDeadCandidatesPage(snapshotId, null, "src/EntryPointProbe/Program.cs", null, false, false, false, 200, null);

            Assert.Equal(2, page.Candidates.Count);

            var methodEntry = Assert.Single(page.Candidates, c => c.SymbolId.Split('|')[0] == "M:Program.{Main}$(System.String[])");
            var typeEntry = Assert.Single(page.Candidates, c => c.SymbolId.Split('|')[0] == "T:Program");

            Assert.Equal(DeadCandidateStatus.UncertainDead, methodEntry.Status);
            Assert.Equal(DeadCandidateReason.EntryPointConvention, methodEntry.Reason);
            Assert.Single(methodEntry.Uncertainties);

            Assert.Equal(DeadCandidateStatus.UncertainDead, typeEntry.Status);
            Assert.Equal(DeadCandidateReason.EntryPointConvention, typeEntry.Reason);
            Assert.Single(typeEntry.Uncertainties);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task ProvedLive_ViaCallsReadsWritesConstructs()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Counter.cs"] = """
                             namespace TestProject;

                             public class Counter
                             {
                                 private int _v;
                                 public int Get() => _v;
                                 public void Set(int x) => _v = x;
                             }

                             public class Consumer
                             {
                                 public int Use() => new Counter().Get();
                                 public void Make() { var c = new Counter(); c.Set(1); }
                             }
                             """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);

        var counterTypeId = ResolveSymbolId(snapshotId, "global::TestProject.Counter");
        var getId = ResolveSymbolId(snapshotId, "global::TestProject.Counter.Get");
        var fieldId = ResolveSymbolId(snapshotId, "global::TestProject.Counter._v");

        // Sanity: the fixture actually produced the LIVE edges this test relies on.
        Assert.NotEmpty(QueryEdges(snapshotId, nameof(EdgeKind.Reads), Provenance.CompilerProved));
        Assert.NotEmpty(QueryEdges(snapshotId, nameof(EdgeKind.Writes), Provenance.CompilerProved));
        Assert.NotEmpty(QueryEdges(snapshotId, nameof(EdgeKind.Constructs), Provenance.CompilerProved));
        Assert.NotEmpty(QueryEdges(snapshotId, nameof(EdgeKind.Calls), Provenance.CompilerProved));

        using var store = OpenStore(DbPath);
        try
        {
            // include_public=true so a false "alive" verdict can't hide behind the
            // Q1 exclusion — if any of these leaked through as dead they would still
            // show up here as public_surface.
            var page = store.GetDeadCandidatesPage(snapshotId, null, null, null, true, false, false, 200, null);

            Assert.DoesNotContain(page.Candidates, c => c.SymbolId == counterTypeId);
            Assert.DoesNotContain(page.Candidates, c => c.SymbolId == getId);
            Assert.DoesNotContain(page.Candidates, c => c.SymbolId == fieldId);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task ProjectIdentity_RenamedAndMultiTargeted_SameAnswerAsPlainProject()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        // B18: Three projects with internal attribute-free properties, using different names
        CreateProject("PlainLib", new Dictionary<string, string>
        {
            ["PlainLib.cs"] = """
namespace PlainLib;

internal class PlainLibClass
{
    internal int Value { get; set; }
}
"""
        });
        
        CreateProject("RenamedLib", new Dictionary<string, string>
        {
            ["RenamedLib.cs"] = """
namespace RenamedLib;

internal class RenamedLibClass
{
    internal int Value { get; set; }
}
"""
        }, msbuildProperties: new Dictionary<string, string> { ["AssemblyName"] = "Renamed.Lib" });
        
        CreateProject("MultiLib", new Dictionary<string, string>
        {
            ["MultiLib.cs"] = """
namespace MultiLib;

internal class MultiLibClass
{
    internal int Value { get; set; }
}
"""}, targetFramework: "net9.0;net10.0");

        await RestoreSolutionAsync();
        var snapshotId = await RunFullIndexAsync(DbPath);

        using var store = OpenStore(DbPath);
        try
        {
            // Query once with includePublic = false. The project filter is null:
            // the store matches it exactly against the assembly name, so a renamed project's
            // project name would return an empty page and hide the status check (B18).
            var page = store.GetDeadCandidatesPage(snapshotId, null, null, "Property", false, false, false, 200, null);

            // Assert all three have the same result
            var plainEntry = Assert.Single(page.Candidates, c => c.SymbolId.Contains("PlainLibClass.Value"));
            var renamedEntry = Assert.Single(page.Candidates, c => c.SymbolId.Contains("RenamedLibClass.Value"));
            var multiEntry = Assert.Single(page.Candidates, c => c.SymbolId.Contains("MultiLibClass.Value"));

            // All should be UncertainDead with SerializationConvention reason
            Assert.Equal(DeadCandidateStatus.UncertainDead, plainEntry.Status);
            Assert.Equal(DeadCandidateReason.SerializationConvention, plainEntry.Reason);

            Assert.Equal(DeadCandidateStatus.UncertainDead, renamedEntry.Status);
            Assert.Equal(DeadCandidateReason.SerializationConvention, renamedEntry.Reason);

            Assert.Equal(DeadCandidateStatus.UncertainDead, multiEntry.Status);
            Assert.Equal(DeadCandidateReason.SerializationConvention, multiEntry.Reason);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task ProjectIdentity_RenamedProject_ProjectLevelBindingRecordMarksCandidatesUnresolved()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        // B18: One renamed project with binding incompleteness record
        CreateProject("RenamedLib2", new Dictionary<string, string>
        {
            ["RenamedLib2.cs"] = """
namespace RenamedLib2;

internal class Helper
{
    internal void HelperMethod() { }
}
"""
        }, msbuildProperties: new Dictionary<string, string> { ["AssemblyName"] = "Renamed.Lib2" });

        var snapshotId = await RunFullIndexAsync(DbPath);
        var helperId = ResolveSymbolId(snapshotId, "global::RenamedLib2.Helper.HelperMethod");

        using var store = OpenStore(DbPath);
        try
        {
            // Insert binding incompleteness record for project level
            store.SaveBindingIncompleteness(snapshotId,
            [
                new BindingIncompletenessRecord("RenamedLib2", null, "unsupported_syntax", 1, "0.0.0")
            ]);

            // Project filter is null: the store matches it exactly against the assembly
            // name, and this project is renamed (B18).
            var page = store.GetDeadCandidatesPage(snapshotId, null, null, "Method", false, false, false, 200, null);

            var entry = Assert.Single(page.Candidates, c => c.SymbolId == helperId);
            Assert.Equal(DeadCandidateStatus.Unresolved, entry.Status);
            Assert.Equal(DeadCandidateReason.BindingIncompleteness, entry.Reason);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task TestProjectDetection_UsesReferences_NotNames()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        // F14: Test project detection uses references, not names
        CreateProject("Foo.UnitTests", new Dictionary<string, string>
        {
            ["Helper.cs"] = """
namespace Foo.UnitTests;

internal static class Helper
{
    internal static void Unused() { }
}
"""
        }, packageReferences: new[] { "xunit@2.9.3" });
        
        CreateProject("Bar.Tests", new Dictionary<string, string>
        {
            ["Helper2.cs"] = """
namespace Bar.Tests;

internal static class Helper2
{
    internal static void Unused() { }
}
"""
        });

        await RestoreSolutionAsync();
        var snapshotId = await RunFullIndexAsync(DbPath);

        using var store = OpenStore(DbPath);
        try
        {
            // Default query (includeTests = false): only Bar.Tests should appear as proved_dead
            var defaultPage = store.GetDeadCandidatesPage(snapshotId, null, null, "Method", false, false, false, 200, null);
            var fooUnused = defaultPage.Candidates.FirstOrDefault(c => c.SymbolId.Contains("Foo.UnitTests.Helper.Unused"));
            var barUnused = defaultPage.Candidates.FirstOrDefault(c => c.SymbolId.Contains("Bar.Tests.Helper2.Unused"));
            
            Assert.Null(fooUnused); // Should not appear because it's a test project and includeTests = false
            Assert.NotNull(barUnused); // Should appear because it's not a test project
            Assert.Equal(DeadCandidateStatus.ProvedDead, barUnused.Status);

            // Query with includeTests = true: the test project's entry surfaces as test_harness,
            // while a project whose name merely ends in .Tests stays an ordinary proved_dead candidate.
            var testPage = store.GetDeadCandidatesPage(snapshotId, null, null, "Method", false, false, true, 200, null);
            var fooUncertain = Assert.Single(testPage.Candidates, c => c.SymbolId.Contains("Foo.UnitTests.Helper.Unused"));
            var barProved = Assert.Single(testPage.Candidates, c => c.SymbolId.Contains("Bar.Tests.Helper2.Unused"));

            Assert.Equal(DeadCandidateStatus.Uncertain, fooUncertain.Status);
            Assert.Equal(DeadCandidateReason.TestHarness, fooUncertain.Reason);
            Assert.Equal(DeadCandidateStatus.ProvedDead, barProved.Status);
            Assert.Equal(DeadCandidateReason.NoIncomingLiveEdges, barProved.Reason);
        }
        finally
        {
            store.Close();
        }
    }

    [Fact]
    public void DeadCandidateStore_HasNoHardcodedProjectNameRule()
    {
        AssertStoreSourceHasNoHardcodedProjectName();
    }

    [SkippableFact]
    public async Task ProcessEntryPoint_ExplicitMain_TypeIsUncertain()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        // F13: New test for explicit Main method - type should be UncertainDead, not ProvedDead
        CreateProject("EntryPointProbe", new Dictionary<string, string>
        {
            ["Program.cs"] = """
namespace EntryPointProbe;

internal class Program
{
    static void Main() { }
}
"""
        }, msbuildProperties: new Dictionary<string, string> { ["OutputType"] = "Exe" });

        var snapshotId = await RunFullIndexAsync(DbPath);

        using var store = OpenStore(DbPath);
        try
        {
            var programTypeId = ResolveSymbolId(snapshotId, "global::EntryPointProbe.Program");
            var mainMethodId = ResolveSymbolId(snapshotId, "global::EntryPointProbe.Program.Main");

            // Project filter is null; the document filter isolates Program.cs (F13).
            var page = store.GetDeadCandidatesPage(snapshotId, null, "src/EntryPointProbe/Program.cs", null, false, false, false, 200, null);

            // Should have exactly two entries: the method and the type
            Assert.Equal(2, page.Candidates.Count);

            var programType = Assert.Single(page.Candidates, c => c.SymbolId == programTypeId);
            var mainMethod = Assert.Single(page.Candidates, c => c.SymbolId == mainMethodId);

            // Both should be UncertainDead with EntryPointConvention
            Assert.Equal(DeadCandidateStatus.UncertainDead, programType.Status);
            Assert.Equal(DeadCandidateReason.EntryPointConvention, programType.Reason);
            Assert.Single(programType.Uncertainties);
            Assert.Equal(DeadCandidateStatus.UncertainDead, mainMethod.Status);
            Assert.Equal(DeadCandidateReason.EntryPointConvention, mainMethod.Reason);
            Assert.Single(mainMethod.Uncertainties);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task PropertyAndEventAccessors_InheritLivenessFromTheirSymbol()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        // B24: Test that property and event accessors inherit liveness from their symbol
        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Holder.cs"] = """
namespace TestProject;

internal sealed class Holder
{
    internal int Written { get; private set; }      // setter written, getter read inside Run
    internal int OnlyRead { get; private set; }     // getter read, setter never written
    internal int this[int i] => i;                  // indexer getter read
    internal event System.EventHandler? Ev { add { } remove { } }   // custom accessors are declared
    internal int Weak { get; set; }                 // gets an injected name_candidate edge below
    internal void Run()
    {
        Written = 1;
        System.Console.WriteLine(Written);
        System.Console.WriteLine(OnlyRead);
        System.Console.WriteLine(this[1]);
    }
}
internal static class Entry { internal static void Go() => new Holder().Run(); }
"""
        });

        var snapshotId = await RunFullIndexAsync(DbPath);

        using var store = OpenStore(DbPath);
        try
        {
            // Inject edges to test the inheritance rules
            store.SaveEdges(snapshotId,
            [
                new EdgeRecord
                {
                    SourceSymbolId = "route://injected/writes",
                    TargetSymbolId = ResolveSymbolId(snapshotId, "global::TestProject.Holder.Ev"),
                    Kind = nameof(EdgeKind.Writes),
                    Provenance = Provenance.CompilerProved,
                    ExtractorVersion = "0.0.0-injected",
                    SourceDocumentPath = "src/TestProject/Injected.cs",
                    SourceStartLine = 1,
                    SourceStartColumn = 1,
                    SourceEndLine = 1,
                    SourceEndColumn = 1
                },
                new EdgeRecord
                {
                    SourceSymbolId = "name://injected/candidate",
                    TargetSymbolId = ResolveSymbolId(snapshotId, "global::TestProject.Holder.Weak"),
                    Kind = nameof(EdgeKind.ReflectionNameCandidate),
                    Provenance = Provenance.NameCandidate,
                    ExtractorVersion = "0.0.0-injected",
                    SourceDocumentPath = "src/TestProject/Injected.cs",
                    SourceStartLine = 1,
                    SourceStartColumn = 1,
                    SourceEndLine = 1,
                    SourceEndColumn = 1
                }
            ]);

            var page = store.GetDeadCandidatesPage(snapshotId, null, null, "Method", false, false, false, 200, null);

            // Accessors should NOT be in candidates (they inherit liveness)
            var getWritten = page.Candidates.FirstOrDefault(c => c.SymbolId.Contains("get_Written"));
            var setWritten = page.Candidates.FirstOrDefault(c => c.SymbolId.Contains("set_Written"));
            var getOnlyRead = page.Candidates.FirstOrDefault(c => c.SymbolId.Contains("get_OnlyRead"));
            var getItem = page.Candidates.FirstOrDefault(c => c.SymbolId.Contains("get_Item"));
            var addEv = page.Candidates.FirstOrDefault(c => c.SymbolId.Contains("add_Ev"));
            var removeEv = page.Candidates.FirstOrDefault(c => c.SymbolId.Contains("remove_Ev"));

            Assert.Null(getWritten); // Not in candidates (inherits from Written property)
            Assert.Null(setWritten); // Not in candidates (inherits from Written property)
            Assert.Null(getOnlyRead); // Not in candidates (inherits from OnlyRead property)
            Assert.Null(getItem); // Not in candidates (inherits from indexer)
            Assert.Null(addEv); // Not in candidates (inherits from Ev event)
            Assert.Null(removeEv); // Not in candidates (inherits from Ev event)

            // set_OnlyRead should be ProvedDead (setter whose property is never written)
            var setOnlyRead = Assert.Single(page.Candidates, c => c.SymbolId.Contains("set_OnlyRead"));
            Assert.Equal(DeadCandidateStatus.ProvedDead, setOnlyRead.Status);
            Assert.Equal(DeadCandidateReason.NoIncomingLiveEdges, setOnlyRead.Reason);

            // get_Weak should be UncertainDead with NameCandidate (weak inherited edge)
            var getWeak = Assert.Single(page.Candidates, c => c.SymbolId.Contains("get_Weak"));
            Assert.Equal(DeadCandidateStatus.UncertainDead, getWeak.Status);
            Assert.Equal(DeadCandidateReason.NameCandidate, getWeak.Reason);
            Assert.Single(getWeak.Uncertainties); // Should have one uncertainty about the weak inherited edge

            // set_Weak should be ProvedDead (the injected edge is not a Writes edge)
            var setWeak = Assert.Single(page.Candidates, c => c.SymbolId.Contains("set_Weak"));
            Assert.Equal(DeadCandidateStatus.ProvedDead, setWeak.Status);
            Assert.Equal(DeadCandidateReason.NoIncomingLiveEdges, setWeak.Reason);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task ExtensionBlockProperty_ImplementationAccessorInheritsLiveness()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        // B24: the read of t.Len gives Reads to the block property; the implementation
        // accessor on the outer static class must inherit that liveness through
        // associated_symbol, so it is not a candidate.
        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Text.cs"] = """
                          namespace TestProject;

                          internal sealed class Text { }
                          """,
            ["TextExt.cs"] = """
                             namespace TestProject;

                             public static class TextExt
                             {
                                 extension(Text t)
                                 {
                                     public int Len => 3;
                                 }
                             }
                             """,
            ["UseExt.cs"] = """
                            namespace TestProject;

                            internal static class UseExt { internal static int Go(Text t) => t.Len; }
                            """
        });

        var snapshotId = await RunFullIndexAsync(DbPath);

        using var store = OpenStore(DbPath);
        try
        {
            var page = store.GetDeadCandidatesPage(snapshotId, null, null, "Method", true, false, false, 200, null);

            Assert.DoesNotContain(page.Candidates, c => c.SymbolId.Contains(".get_Len(", StringComparison.Ordinal));
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task TypeLiveness_InheritsFromMembersAndTypeUses()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        // B25: Test that type liveness inherits from members and type uses
        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Helper.cs"] = """
namespace TestProject;

internal static class Helper { internal static void Do() { } }              // static class, called method
internal enum Mode { A, B }                                                 // member A is read
internal class BaseOnly { }
internal interface IMark { }
internal sealed class Derived : BaseOnly, IMark { }                         // constructed; base and interface are used through it
internal sealed class SigOnly { }                                           // used only as a parameter type
internal static class Outer2 { internal static class Inner2 { internal static void Do() { } } }   // nested chain
internal sealed class SelfOnly { internal SelfOnly Next() => this; }        // only its own member refers to it
internal sealed class Truly { }                                              // never used
internal static class Host
{
    internal static void Run(SigOnly s) { Helper.Do(); var m = Mode.A; _ = new Derived(); Outer2.Inner2.Do(); System.Console.WriteLine(m); }
}
public static class Api { public static void Go() => Host.Run(null!); }
"""
        });

        var snapshotId = await RunFullIndexAsync(DbPath);

        using var store = OpenStore(DbPath);
        try
        {
            var page = store.GetDeadCandidatesPage(snapshotId, null, null, "Type", false, false, false, 200, null);

            // These types should NOT be in candidates (they have live incoming edges)
            var helperType = page.Candidates.FirstOrDefault(c => c.SymbolId.Contains("T:TestProject.Helper"));
            var modeType = page.Candidates.FirstOrDefault(c => c.SymbolId.Contains("T:TestProject.Mode"));
            var baseOnlyType = page.Candidates.FirstOrDefault(c => c.SymbolId.Contains("T:TestProject.BaseOnly"));
            var iMarkType = page.Candidates.FirstOrDefault(c => c.SymbolId.Contains("T:TestProject.IMark"));
            var derivedType = page.Candidates.FirstOrDefault(c => c.SymbolId.Contains("T:TestProject.Derived"));
            var sigOnlyType = page.Candidates.FirstOrDefault(c => c.SymbolId.Contains("T:TestProject.SigOnly"));
            var outer2Type = page.Candidates.FirstOrDefault(c => c.SymbolId.Split('|')[0] == "T:TestProject.Outer2");
            var inner2Type = page.Candidates.FirstOrDefault(c => c.SymbolId.Split('|')[0] == "T:TestProject.Outer2.Inner2");
            var hostType = page.Candidates.FirstOrDefault(c => c.SymbolId.Contains("T:TestProject.Host"));

            Assert.Null(helperType); // Not in candidates (called by Host.Run)
            Assert.Null(modeType); // Not in candidates (mode.A is read)
            Assert.Null(baseOnlyType); // Not in candidates (used through Derived)
            Assert.Null(iMarkType); // Not in candidates (implemented through Derived)
            Assert.Null(derivedType); // Not in candidates (constructed)
            Assert.Null(sigOnlyType); // Not in candidates (used as parameter)
            Assert.Null(outer2Type); // Not in candidates (Outer2.Inner2.Do is called)
            Assert.Null(inner2Type); // Not in candidates (nested type used through Outer2)
            Assert.Null(hostType); // Not in candidates (Api.Go is called)

            // SelfOnly and Truly should be ProvedDead with NoIncomingLiveEdges
            var selfOnlyType = Assert.Single(page.Candidates, c => c.SymbolId.Contains("T:TestProject.SelfOnly"));
            Assert.Equal(DeadCandidateStatus.ProvedDead, selfOnlyType.Status);
            Assert.Equal(DeadCandidateReason.NoIncomingLiveEdges, selfOnlyType.Reason);

            var trulyType = Assert.Single(page.Candidates, c => c.SymbolId.Contains("T:TestProject.Truly"));
            Assert.Equal(DeadCandidateStatus.ProvedDead, trulyType.Status);
            Assert.Equal(DeadCandidateReason.NoIncomingLiveEdges, trulyType.Reason);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task TypeLiveness_DeadTypeWithSelfCallingMembersStaysDead()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        // B25: a call from inside the type does not lift the type — the dead type
        // whose members only call each other stays ProvedDead.
        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Island.cs"] = """
                            namespace TestProject;

                            internal static class Island { internal static void A() => B(); internal static void B() { } }
                            """
        });

        var snapshotId = await RunFullIndexAsync(DbPath);

        using var store = OpenStore(DbPath);
        try
        {
            var page = store.GetDeadCandidatesPage(snapshotId, null, null, "Type", false, false, false, 200, null);

            var islandType = Assert.Single(page.Candidates, c => c.SymbolId.Split('|')[0] == "T:TestProject.Island");
            Assert.Equal(DeadCandidateStatus.ProvedDead, islandType.Status);
            Assert.Equal(DeadCandidateReason.NoIncomingLiveEdges, islandType.Reason);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task CompilerAndRuntimeCalledMembers_AreNotProvedDead()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        // B26: Test that compiler and runtime called members are not proved dead
        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Lifecycle.cs"] = """
namespace TestProject;

internal sealed class Lifecycle { static Lifecycle() { } internal static void Touch() { } }
""",
            ["Bag.cs"] = """
namespace TestProject;

internal sealed class Bag : System.Collections.IEnumerable
{
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => System.Array.Empty<int>().GetEnumerator();
}
""",
            ["Rec.cs"] = """
namespace TestProject;

internal sealed record Rec { internal int A { get; init; } }
""",
            ["Use.cs"] = """
namespace TestProject;

internal static class Use { internal static void Go() { Lifecycle.Touch(); _ = new Bag(); _ = new Rec(); } }
"""
        });

        var snapshotId = await RunFullIndexAsync(DbPath);

        using var store = OpenStore(DbPath);
        try
        {
            var page = store.GetDeadCandidatesPage(snapshotId, null, null, "Method", true, false, false, 200, null);

            // No entry whose symbol id contains Lifecycle.#cctor (static constructor should not be proved dead)
            var cctor = page.Candidates.FirstOrDefault(c => c.SymbolId.Contains("Lifecycle.#cctor"));
            Assert.Null(cctor);

            // The Bag explicit implementation entry should have UncertainDead with external_interface_implementation
            var bagGetEnumerator = page.Candidates.FirstOrDefault(c => c.SymbolId.Contains("System#Collections#IEnumerable#GetEnumerator"));
            Assert.NotNull(bagGetEnumerator);
            Assert.Equal(DeadCandidateStatus.UncertainDead, bagGetEnumerator.Status);
            Assert.Equal("external_interface_implementation", bagGetEnumerator.Reason);

            // No entry for any of the Rec members that are compiler-synthesized
            var recMembers = new[] { "PrintMembers", "Equals", "GetHashCode", "ToString", "{Clone}$", "op_Equality", "op_Inequality", "get_EqualityContract" }
                .SelectMany(member => page.Candidates.Where(c => c.SymbolId.Contains($"Rec.{member}")));
            Assert.Empty(recMembers);
        }
        finally
        {
            store.Close();
        }
    }

    /// <summary>
    ///     Companion to <see cref="PossibleDispatch_InheritedOnly_Uncertain" />:
    ///     proves, on real compiled code with no injected edges, that an inherited
    ///     (non-direct) implicit interface implementation produces exactly one
    ///     MayDispatchTo edge with "possible" provenance, and that
    ///     GetDeadCandidatesPage classifies its target as public_surface (Q1) rather
    ///     than possible_dispatch — public_surface wins because it is evaluated
    ///     first in the ladder and the target must be public to implicitly satisfy
    ///     an interface.
    /// </summary>
    [SkippableFact]
    public async Task PossibleDispatch_InheritedOnly_SurfacesAsPublicSurface()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Worker.cs"] = """
                            namespace TestProject;

                            public interface IWorker { void DoWork(); }

                            public class WorkerBase
                            {
                                public void DoWork() { }
                            }

                            public class Worker : WorkerBase, IWorker
                            {
                            }
                            """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var doWorkId = ResolveSymbolId(snapshotId, "global::TestProject.WorkerBase.DoWork");

        var dispatch = Assert.Single(QueryEdges(snapshotId, nameof(EdgeKind.MayDispatchTo), Provenance.Possible));
        Assert.Equal(doWorkId, dispatch.TargetSymbolId);
        Assert.DoesNotContain(QueryEdges(snapshotId, nameof(EdgeKind.MayDispatchTo), Provenance.CompilerProved),
            e => e.TargetSymbolId == doWorkId);

        using var store = OpenStore(DbPath);
        try
        {
            var defaultPage = store.GetDeadCandidatesPage(snapshotId, null, null, null, false, false, false, 200, null);
            Assert.DoesNotContain(defaultPage.Candidates, c => c.SymbolId == doWorkId);

            var publicPage = store.GetDeadCandidatesPage(snapshotId, null, null, null, true, false, false, 200, null);
            var entry = Assert.Single(publicPage.Candidates, c => c.SymbolId == doWorkId);
            Assert.Equal(DeadCandidateStatus.UncertainDead, entry.Status);
            Assert.Equal(DeadCandidateReason.PublicSurface, entry.Reason);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task Convention_NameCandidate_RuntimeUnknown_Uncertain()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Targets.cs"] = """
                             namespace TestProject;

                             internal class Targets
                             {
                                 internal void ConventionTarget() { }
                                 internal void NameCandidateTarget() { }
                                 internal void RuntimeUnknownTarget() { }
                             }
                             """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);

        var conventionId = ResolveSymbolId(snapshotId, "global::TestProject.Targets.ConventionTarget");
        var nameCandidateId = ResolveSymbolId(snapshotId, "global::TestProject.Targets.NameCandidateTarget");
        var runtimeUnknownId = ResolveSymbolId(snapshotId, "global::TestProject.Targets.RuntimeUnknownTarget");

        using var store = OpenStore(DbPath);
        try
        {
            store.SaveEdges(snapshotId,
            [
                MakeEdge("route://synthetic/convention", conventionId, nameof(EdgeKind.Registers),
                    Provenance.Convention),
                MakeEdge("name://synthetic/candidate", nameCandidateId, nameof(EdgeKind.ReflectionNameCandidate),
                    Provenance.NameCandidate),
                MakeEdge("hosted://synthetic/service", runtimeUnknownId, nameof(EdgeKind.Registers),
                    Provenance.RuntimeUnknown)
            ]);

            var page = store.GetDeadCandidatesPage(snapshotId, null, null, null, false, false, false, 200, null);

            var conventionEntry = Assert.Single(page.Candidates, c => c.SymbolId == conventionId);
            Assert.Equal(DeadCandidateStatus.UncertainDead, conventionEntry.Status);
            Assert.Equal(DeadCandidateReason.FrameworkConvention, conventionEntry.Reason);
            Assert.Contains("inferred by naming convention", Assert.Single(conventionEntry.Uncertainties).Description);

            var nameCandidateEntry = Assert.Single(page.Candidates, c => c.SymbolId == nameCandidateId);
            Assert.Equal(DeadCandidateStatus.UncertainDead, nameCandidateEntry.Status);
            Assert.Equal(DeadCandidateReason.NameCandidate, nameCandidateEntry.Reason);
            Assert.Contains("matched by name", Assert.Single(nameCandidateEntry.Uncertainties).Description);

            var runtimeUnknownEntry = Assert.Single(page.Candidates, c => c.SymbolId == runtimeUnknownId);
            Assert.Equal(DeadCandidateStatus.Unresolved, runtimeUnknownEntry.Status);
            Assert.Equal(DeadCandidateReason.RuntimeUnknown, runtimeUnknownEntry.Reason);
            Assert.Contains("DeclaredBoundaries.Known", Assert.Single(runtimeUnknownEntry.Uncertainties).Description);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task BindingIncompleteness_Overlap_Unresolved()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Util.cs"] = """
                          namespace TestProject;

                          internal static class Util
                          {
                              internal static void Helper() { }
                          }
                          """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var helperId = ResolveSymbolId(snapshotId, "global::TestProject.Util.Helper");
        const string helperFile = "src/TestProject/Util.cs";

        using var store = OpenStore(DbPath);
        try
        {
            // "unsupported_syntax" is one of BindingIncompletenessReason.UnobservableReasons
            // (BindingIncompletenessCollector.cs:30-41) — overlap must force `unresolved`,
            // never `proved_dead`, per the ladder's Q4/binding-incompleteness tier.
            store.SaveBindingIncompleteness(snapshotId,
            [
                new BindingIncompletenessRecord("TestProject", helperFile, "unsupported_syntax", 1, "0.0.0")
            ]);

            var page = store.GetDeadCandidatesPage(snapshotId, null, null, null, false, false, false, 200, null);

            var entry = Assert.Single(page.Candidates, c => c.SymbolId == helperId);
            Assert.Equal(DeadCandidateStatus.Unresolved, entry.Status);
            Assert.Equal(DeadCandidateReason.BindingIncompleteness, entry.Reason);
            Assert.NotEqual(DeadCandidateStatus.ProvedDead, entry.Status);
            var uncertainty = Assert.Single(entry.Uncertainties);
            Assert.Contains("could not be completed because the extractor does not support the relevant syntax",
                uncertainty.Description);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task PublicSurface_ExcludedByDefault_FlaggedOnOptIn()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Api.cs"] = """
                         namespace TestProject;

                         public class Api
                         {
                             public void Endpoint() { }
                         }

                         internal class Caller
                         {
                             internal void Use() { }
                         }
                         """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var endpointId = ResolveSymbolId(snapshotId, "global::TestProject.Api.Endpoint");

        using var store = OpenStore(DbPath);
        try
        {
            var defaultPage = store.GetDeadCandidatesPage(snapshotId, null, null, null, false, false, false, 200, null);
            Assert.DoesNotContain(defaultPage.Candidates, c => c.SymbolId == endpointId);

            var publicPage = store.GetDeadCandidatesPage(snapshotId, null, null, null, true, false, false, 200, null);
            var entry = Assert.Single(publicPage.Candidates, c => c.SymbolId == endpointId);
            Assert.Equal(DeadCandidateStatus.UncertainDead, entry.Status);
            Assert.Equal(DeadCandidateReason.PublicSurface, entry.Reason);
            Assert.Contains("Public/protected member", Assert.Single(entry.Uncertainties).Description);
            Assert.True(publicPage.UncertainCount >= 1);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task IncludeGenerated_ToggleBehavior()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Gen.g.cs"] = """
                           // <auto-generated/>
                           namespace TestProject;

                           internal class Generated
                           {
                               internal void M() { }
                           }
                           """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var mId = ResolveSymbolId(snapshotId, "global::TestProject.Generated.M");

        using var store = OpenStore(DbPath);
        try
        {
            var defaultPage = store.GetDeadCandidatesPage(snapshotId, null, null, null, false, false, false, 200, null);
            Assert.DoesNotContain(defaultPage.Candidates, c => c.SymbolId == mId);

            var generatedPage = store.GetDeadCandidatesPage(snapshotId, null, null, null, false, true, false, 200, null);
            var entry = Assert.Single(generatedPage.Candidates, c => c.SymbolId == mId);
            Assert.True(entry.IsGenerated);
            Assert.Equal(DeadCandidateStatus.Uncertain, entry.Status);
            Assert.Equal(DeadCandidateReason.GeneratedExcluded, entry.Reason);
            Assert.Contains("includeGenerated is set to false", Assert.Single(entry.Uncertainties).Description);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task BatchedQuery_NoPerCandidateGetIncomingEdges()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        // Architectural invariant (Q5, lurp-phases-report.md Phase 3): the
        // dead-candidate store must never call IEdgeStore.GetIncomingEdges per
        // candidate — incoming LIVE edges are fetched via one batched
        // `target_symbol_id IN (...)` query per page
        // (DeadCandidateStore.FetchIncomingLiveEdgesBatched), chunked at 900 under
        // SQLITE_MAX_VARIABLE_NUMBER=999. Verified two ways: (1) a static source
        // scan below asserts the per-candidate call site does not exist in
        // DeadCandidateStore.cs; (2) a multi-candidate page still returns correct
        // per-candidate results, so the batching isn't silently dropping rows.
        AssertNoPerCandidateGetIncomingEdgesCallSite();

        var files = new Dictionary<string, string>();
        for (var i = 0; i < 12; i++)
            files[$"Dead{i}.cs"] = $$"""
                                     namespace TestProject;

                                     internal class Dead{{i}}
                                     {
                                         internal void Unused() { }
                                     }
                                     """;
        files["Live.cs"] = """
                           namespace TestProject;

                           internal class LiveTarget
                           {
                               internal void Used() { }
                           }

                           internal class Caller
                           {
                               internal void Call() => new LiveTarget().Used();
                           }
                           """;
        CreateProject("TestProject", files);
        var snapshotId = await RunFullIndexAsync(DbPath);
        var usedId = ResolveSymbolId(snapshotId, "global::TestProject.LiveTarget.Used");

        using var store = OpenStore(DbPath);
        try
        {
            var page = store.GetDeadCandidatesPage(snapshotId, null, null, "Method", false, false, false, 200, null);

            Assert.DoesNotContain(page.Candidates, c => c.SymbolId == usedId);
            for (var i = 0; i < 12; i++)
            {
                var deadId = ResolveSymbolId(snapshotId, $"global::TestProject.Dead{i}.Unused");
                var entry = Assert.Single(page.Candidates, c => c.SymbolId == deadId);
                Assert.Equal(DeadCandidateStatus.ProvedDead, entry.Status);
            }

            Assert.True(page.DeadCount >= 12);
            Assert.Null(page.NextCursor);
        }
        finally
        {
            store.Close();
        }
    }

    /// <summary>
    ///     Pins declaration lookup across documents (DeadCandidateStore.FetchDeclarationInfo):
    ///     a partial class declared in two separate documents must report both locations in
    ///     ordinal relative_path order, line-mapped from the per-document blob cache, and a
    ///     generated document's symbol must stay excluded by default and surface as
    ///     generated_excluded when includeGenerated is set.
    /// </summary>
    [SkippableFact]
    public async Task PartialAcrossTwoDocuments_OrderedLocations_AndGeneratedSymbol()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["ZPart.cs"] = """
                           namespace TestProject;

                           internal partial class Widget
                           {
                               internal void FromZPart() { }
                           }
                           """,
            ["APart.cs"] = """
                           namespace TestProject;

                           internal partial class Widget
                           {
                               internal void FromAPart() { }
                           }
                           """,
            ["Gen.g.cs"] = """
                           // <auto-generated/>
                           namespace TestProject;

                           internal class Generated
                           {
                               internal void M() { }
                           }
                           """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var widgetId = ResolveSymbolId(snapshotId, "global::TestProject.Widget");
        var fromAId = ResolveSymbolId(snapshotId, "global::TestProject.Widget.FromAPart");
        var generatedId = ResolveSymbolId(snapshotId, "global::TestProject.Generated.M");

        using var store = OpenStore(DbPath);
        try
        {
            var page = store.GetDeadCandidatesPage(snapshotId, null, null, null, false, false, false, 200, null);

            // Page order is symbol_id ASC (ordinal).
            var pageIds = page.Candidates.Select(c => c.SymbolId).ToList();
            Assert.Equal(pageIds.OrderBy(id => id, StringComparer.Ordinal).ToList(), pageIds);

            // The partial type carries one declaration per document, ordered by relative_path
            // (APart.cs < ZPart.cs), each with a mapped start line.
            var widget = Assert.Single(page.Candidates, c => c.SymbolId == widgetId);
            Assert.False(widget.IsGenerated);
            Assert.Equal(2, widget.DeclarationCount);
            Assert.Equal(2, widget.Locations.Count);
            Assert.Equal("src/TestProject/APart.cs", widget.Locations[0].DocumentPath);
            Assert.Equal("src/TestProject/ZPart.cs", widget.Locations[1].DocumentPath);
            Assert.Equal("src/TestProject/APart.cs", widget.DocumentPath);
            Assert.True(widget.Locations[0].StartLine > 0);
            Assert.True(widget.Locations[1].StartLine > 0);
            Assert.False(widget.Locations[0].IsGenerated);
            Assert.False(widget.Locations[1].IsGenerated);

            var fromA = Assert.Single(page.Candidates, c => c.SymbolId == fromAId);
            Assert.Single(fromA.Locations);
            Assert.Equal("src/TestProject/APart.cs", fromA.Locations[0].DocumentPath);
            Assert.True(fromA.Locations[0].StartLine > 0);

            // Generated symbols stay out by default and are flagged on opt-in.
            Assert.DoesNotContain(page.Candidates, c => c.SymbolId == generatedId);
            var generatedPage = store.GetDeadCandidatesPage(snapshotId, null, null, null, false, true, false, 200, null);
            var generated = Assert.Single(generatedPage.Candidates, c => c.SymbolId == generatedId);
            Assert.True(generated.IsGenerated);
            Assert.Equal(DeadCandidateStatus.Uncertain, generated.Status);
            Assert.Equal(DeadCandidateReason.GeneratedExcluded, generated.Reason);
        }
        finally
        {
            store.Close();
        }
    }

    /// <summary>
    ///     B3 step 2a: a C# 14 extension block declares a block member beside its
    ///     implementation on the outer static class. The block method is never
    ///     declared; the compiler-generated marker type is declared and tagged
    ///     is_extension_block, then kept out of the candidate universe. Neither the
    ///     marker type nor a block method is a dead candidate.
    /// </summary>
    [SkippableFact]
    public async Task ExtensionBlock_MarkerTypeAndBlockMethods_AreNotCandidates()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Text.cs"] = """
                          namespace TestProject;

                          public sealed class Text
                          {
                              public string Value { get; set; } = string.Empty;
                          }

                          public static class TextExtensions
                          {
                              extension(Text text)
                              {
                                  public int WordCount() => text.Value.Length;
                              }
                          }
                          """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);

        // The block's marker type is declared and tagged with the metadata key...
        using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM snapshot_symbols
                WHERE snapshot_id = @snapshotId
                  AND metadata_json LIKE '%is_extension_block%';
                """;
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            Assert.True((long)command.ExecuteScalar()! >= 1);
        }

        using var store = OpenStore(DbPath);
        try
        {
            // ...but neither the marker type nor a block method is a candidate.
            var page = store.GetDeadCandidatesPage(snapshotId, null, null, null, true, true, true, 200, null);
            Assert.DoesNotContain(page.Candidates, c => c.SymbolId.Contains("<G>$", StringComparison.Ordinal));
        }
        finally
        {
            store.Close();
        }
    }

    private static void AssertNoPerCandidateGetIncomingEdgesCallSite(
        [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));
        var storeFile = Path.Combine(repoRoot, "src", "Storage", "DeadCandidateStore.cs");
        Assert.True(File.Exists(storeFile), $"Could not locate {storeFile} for the batching invariant scan.");
        var source = File.ReadAllText(storeFile);
        Assert.DoesNotContain("GetIncomingEdges(", source);
    }

    private static void AssertStoreSourceHasNoHardcodedProjectName(
        [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));
        var storeFile = Path.Combine(repoRoot, "src", "Storage", "DeadCandidateStore.cs");
        Assert.True(File.Exists(storeFile), $"Could not locate {storeFile} for the project-name-rule scan.");
        var source = File.ReadAllText(storeFile);
        Assert.DoesNotContain("eNote", source);
    }
}
