namespace Lurp.Tests;

/// <summary>
///     Shared fixture for the impact test classes: builds the ImpactProj fixture and produces
///     an initial full snapshot and an incremental second snapshot of the same project.
/// </summary>
public abstract class ImpactIndexFixture : IntegrationTestBase
{
    protected async Task<string> IndexInitialAsync()
    {
        return await IndexFullAsync("ImpactProj", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                namespace ImpactProj {
                    public class Foo {
                        public void Bar() {}
                        public void Caller() { Bar(); }
                        public void UpstreamCaller() { Caller(); }
                    }
                    public class Extra {
                        public void ExtraMethod() { new Foo().Bar(); }
                    }
                }
                """,
            ["Extra2.cs"] = """
                namespace ImpactProj {
                    public class Another {
                        public void AnotherMethod() { new Foo().Caller(); }
                    }
                }
                """
        });
    }

    /// <summary>
    ///     Builds the direction-default fixture (R141): one production type with a declared
    ///     member and a nested type, one caller of the member, and an xunit test that calls
    ///     it, so one snapshot carries <c>Declares</c> (Target -> Hit), <c>Contains</c>
    ///     (Target -> Inner) and <c>TestedBy</c> (Target -> CallHit) edges.
    /// </summary>
    protected async Task<string> IndexDirectionFixtureAsync()
    {
        CreateProject("DirProj", new Dictionary<string, string>
        {
            ["Target.cs"] = """
                namespace DirProj;
                public class Target
                {
                    public void Hit() { }
                    public class Inner { public void Bump() { } }
                }
                """,
            ["Caller.cs"] = """
                namespace DirProj;
                public class Caller
                {
                    public void Call() { new Target().Hit(); }
                }
                """
        });

        CreateProject("DirProj.Tests", new Dictionary<string, string>
        {
            ["TargetTests.cs"] = """
                using Xunit;
                namespace DirProj;
                public class TargetTests
                {
                    [Fact]
                    public void CallHit() { new Target().Hit(); }
                }
                """
        }, projectReferences: ["DirProj"], packageReferences: ["xunit@2.9.3"]);

        RestoreSolution();
        return await RunFullIndexAsync(DbPath);
    }

    protected async Task<string> IndexSecondAsync()
    {
        WriteFile("ImpactProj", "Models.cs", """
            namespace ImpactProj {
                public class Foo {
                    public void Bar(int x) {}
                    public void Caller() { Bar(1); }
                    public void UpstreamCaller() { Caller(); }
                    public void NewMethod() {}
                }
                public class Extra {
                    public void ExtraMethod() { new Foo().Bar(1); }
                }
            }
            """);
        return await RunIncrementalIndexAsync();
    }
}
