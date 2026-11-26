using Lurp.Adapters;
using Lurp.Storage;

namespace Lurp.Tests;

public sealed partial class GoldenAdapterTests
{
    [Fact]
    public async Task EfModel_FollowsBuilderMethodGroupsAndConverges()
    {
        const string helpers = """
            using Microsoft.EntityFrameworkCore.Metadata.Builders;
            namespace App;
            public class Root { public Nav Navigation { get; set; } = null!; }
            public class Nav;
            public class Owned;
            public static class Helpers
            {
                public static void Configure(EntityTypeBuilder<Root> b) { b.OwnsOne<Owned>("X"); }
            }
            """;
        CreateProject("Helpers", new Dictionary<string, string> { ["Helpers.cs"] = helpers },
            ["Microsoft.EntityFrameworkCore@10.0.13"]);
        CreateProject("App", new Dictionary<string, string>
        {
            ["Context.cs"] = """
                using Microsoft.EntityFrameworkCore;
                using Microsoft.EntityFrameworkCore.Metadata.Builders;
                namespace App;
                public class LocalRoot;
                public class LocalOwned;
                public class Context : DbContext
                {
                    protected override void OnModelCreating(ModelBuilder model)
                    {
                        model.Entity<Root>(Helpers.Configure);
                        model.Entity<LocalRoot>(ConfigureLocal);
                        void ConfigureLocal(EntityTypeBuilder<LocalRoot> b)
                        {
                            b.OwnsOne<LocalOwned>("X");
                            model.Entity<LocalRoot>(ConfigureLocal);
                        }
                    }
                }
                """
        }, ["Microsoft.EntityFrameworkCore@10.0.13"], projectReferences: ["Helpers"]);
        RestoreSolution();
        var initial = await RunFullIndexAsync(DbPath);
        AssertEfMappings(initial, "Context", "LocalOwned", "LocalRoot", "Nav", "Owned", "Root");
        WriteFile("Helpers", "Helpers.cs", helpers.Replace("b.OwnsOne<Owned>(\"X\");", "b.OwnsOne<Owned>(\"X\"); b.Ignore(x => x.Navigation);"));
        var incremental = await RunIncrementalIndexAsync();
        await AssertEfIncrementalParityAsync(incremental);
        AssertEfMappings(incremental, "Context", "LocalOwned", "LocalRoot", "Owned", "Root");
    }

    [Fact]
    public async Task EfModel_CrossFrameworkIgnoresUseStableTypeIdentity()
    {
        CreateProject("Lib", new Dictionary<string, string>
        {
            ["Model.cs"] = """
                using System;
                using System.Linq.Expressions;
                using Microsoft.EntityFrameworkCore;
                using Microsoft.EntityFrameworkCore.Metadata.Builders;
                namespace Microsoft.EntityFrameworkCore
                {
                    public class DbContext { protected virtual void OnModelCreating(ModelBuilder model) { } }
                    public class DbSet<T> where T : class { }
                    public class ModelBuilder
                    {
                        public EntityTypeBuilder<T> Entity<T>() where T : class => new();
                        public void Ignore<T>() where T : class { }
                        public void ApplyConfiguration<T>(IEntityTypeConfiguration<T> config) where T : class { }
                    }
                    public interface IEntityTypeConfiguration<T> where T : class { void Configure(EntityTypeBuilder<T> b); }
                }
                namespace Microsoft.EntityFrameworkCore.Metadata.Builders
                {
                    public class EntityTypeBuilder<T> where T : class
                    {
                        public void Ignore<TProperty>(Expression<Func<T,TProperty>> expression) { }
                    }
                }
                namespace Lib
                {
                    public class Root { public Nav Navigation { get; set; } = null!; }
                    public class Nav { }
                    public class Gone { }
                    public class A { }
                    public class B { }
                    public class Box<T> { }
                    public class Config : IEntityTypeConfiguration<Root>
                    {
                        public void Configure(EntityTypeBuilder<Root> b) { b.Ignore(x => x.Navigation); }
                    }
                    public static class Helper { public static void Remove(ModelBuilder model) { model.Ignore<Gone>(); model.Ignore<Box<A>>(); } }
                }
                """
        }, targetFramework: "net9.0");
        CreateProject("App", new Dictionary<string, string>
        {
            ["Context.cs"] = """
                using Lib;
                using Microsoft.EntityFrameworkCore;
                namespace App;
                public class Context : DbContext
                {
                    public DbSet<Root> Roots { get; set; } = null!;
                    public DbSet<Gone> Gone { get; set; } = null!;
                    public DbSet<Box<B>> Boxes { get; set; } = null!;
                    protected override void OnModelCreating(ModelBuilder model) { model.ApplyConfiguration(new Config()); Helper.Remove(model); }
                }
                """
        }, projectReferences: ["Lib"]);
        RestoreSolution();
        var snapshot = await RunFullIndexAsync(DbPath);
        var context = ResolveSymbolId(snapshot, "global::App.Context");
        var targets = QueryEdges(snapshot, "MapsTo", Provenance.FrameworkDerived)
            .Where(edge => edge.SourceSymbolId == context).Select(edge => edge.TargetSymbolId).ToArray();
        Assert.Contains(ResolveSymbolId(snapshot, "global::Lib.Root"), targets);
        Assert.Contains(ResolveSymbolId(snapshot, "global::Lib.Box<T>"), targets);
        Assert.DoesNotContain(ResolveSymbolId(snapshot, "global::Lib.Nav"), targets);
        Assert.DoesNotContain(ResolveSymbolId(snapshot, "global::Lib.Gone"), targets);
    }

    [Fact]
    public async Task EfModel_UsesOverridesForContextAndConfiguration()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["Model.cs"] = """
                using Microsoft.EntityFrameworkCore;
                using Microsoft.EntityFrameworkCore.Metadata.Builders;
                namespace App;
                public class BaseEntity { public int Id { get; set; } }
                public class DerivedEntity { public int Id { get; set; } }
                public class Root { public int Id { get; set; } public Nav Navigation { get; set; } = null!; public Owned Owned { get; set; } = null!; }
                public class Nav { public int Id { get; set; } }
                public class Owned { public int Id { get; set; } }
                public class TplRoot { public int Id { get; set; } public TplOwned Owned { get; set; } = null!; }
                public class TplOwned { public int Id { get; set; } }
                public abstract class BaseContext : DbContext
                {
                    protected override void OnModelCreating(ModelBuilder model) { Virt(model); model.ApplyConfiguration(new Config()); }
                    protected virtual void Virt(ModelBuilder model) { model.Entity<BaseEntity>(); }
                }
                public class Context : BaseContext { protected override void Virt(ModelBuilder model) { model.Entity<DerivedEntity>(); } }
                public class BaseConfig : IEntityTypeConfiguration<Root>
                {
                    public virtual void Configure(EntityTypeBuilder<Root> builder) { }
                }
                public class Config : BaseConfig
                {
                    public override void Configure(EntityTypeBuilder<Root> builder) { builder.Ignore(x => x.Navigation); builder.OwnsOne(x => x.Owned); builder.HasQueryFilter(x => true); }
                }
                public abstract class Template : IEntityTypeConfiguration<TplRoot>
                {
                    public void Configure(EntityTypeBuilder<TplRoot> builder) { Inner(builder); }
                    protected abstract void Inner(EntityTypeBuilder<TplRoot> builder);
                }
                public class TplConfig : Template
                {
                    protected override void Inner(EntityTypeBuilder<TplRoot> builder) { builder.OwnsOne(x => x.Owned); }
                }
                """
        }, ["Microsoft.EntityFrameworkCore@10.0.13"]);
        RestoreSolution();
        var snapshot = await RunFullIndexAsync(DbPath);
        AssertEfMappings(snapshot, "Context", "DerivedEntity", "Root", "Owned");
        AssertEfMappings(snapshot, "Config", "Root", "Owned");
        AssertEfMappings(snapshot, "TplConfig", "TplRoot", "TplOwned");
        using var store = OpenStore(DbPath);
        var root = ResolveSymbolId(snapshot, "global::App.Root");
        Assert.Contains(store.GetAnnotations(snapshot), a => a.SymbolId == root && a.Kind == "ef_query_filter_constraint");
    }

    [Fact]
    public async Task EfModel_FollowsCapturedLocalFunctionAndPartialMethod()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["Context.cs"] = """
                using Microsoft.EntityFrameworkCore;
                namespace App;
                public class LocalEntity { public int Id { get; set; } }
                public class PartialEntity { public int Id { get; set; } }
                public partial class Context : DbContext
                {
                    protected override void OnModelCreating(ModelBuilder model)
                    {
                        Map();
                        OnModelCreatingPartial(model);
                        void Map()
                        {
                            model.Entity<LocalEntity>().HasQueryFilter(x => true).HasQueryFilter((System.Linq.Expressions.Expression<System.Func<LocalEntity, bool>>?)null);
                            model.Entity<LocalEntity>().HasIndex(x => x.Id).IsUnique();
                            if (false) Map();
                        }
                    }
                    partial void OnModelCreatingPartial(ModelBuilder model);
                }
                public partial class Context
                {
                    partial void OnModelCreatingPartial(ModelBuilder model) { model.Entity<PartialEntity>(); }
                }
                """
        }, ["Microsoft.EntityFrameworkCore@10.0.13"]);
        RestoreSolution();
        var snapshot = await RunFullIndexAsync(DbPath);
        AssertEfMappings(snapshot, "Context", "LocalEntity", "PartialEntity");
        using var store = OpenStore(DbPath);
        var local = ResolveSymbolId(snapshot, "global::App.LocalEntity");
        var annotations = store.GetAnnotations(snapshot).Where(a => a.SymbolId == local).ToList();
        Assert.DoesNotContain(annotations, a => a.Kind == "ef_query_filter_constraint");
        Assert.Contains(annotations, a => a.Kind == "ef_unique_index_constraint");
    }

    [Fact]
    public async Task EfModel_InheritedSetsMapFromConcreteContextsAndClosedProperties()
    {
        const string contexts = """
            using Microsoft.AspNetCore.Identity;
            using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore;
            namespace App;
            public class User : IdentityUser;
            public class IdentityContext : IdentityDbContext<User>;
            public class FirstContext : FirstBase
            {
                protected override void OnModelCreating(ModelBuilder model) { }
            }
            public class SecondContext : GenericContext<Second>;
            """;
        CreateProject("App", new Dictionary<string, string>
        {
            ["Base.cs"] = """
                using Microsoft.EntityFrameworkCore;
                namespace App;
                public abstract class GenericContext<T> : DbContext where T : class
                {
                    public DbSet<T> Open { get; set; } = null!;
                    public DbSet<Box<T>> NestedOpen { get; set; } = null!;
                    public DbSet<Common> Closed { get; set; } = null!;
                    private DbSet<Hidden> Private { get; set; } = null!;
                    public static DbSet<Hidden> Static { get; set; } = null!;
                }
                public abstract class FirstBase : GenericContext<First>
                {
                    protected DbSet<Removed> Removable { get; set; } = null!;
                }
                public class First;
                public class Second;
                public class Removed;
                public class Common;
                public class Hidden;
                public class Box<T>;
                """,
            ["Contexts.cs"] = contexts
        }, ["Microsoft.AspNetCore.Identity.EntityFrameworkCore@10.0.13"]);

        RestoreSolution();
        var initial = await RunFullIndexAsync(DbPath);
        AssertEfMappings(initial, "IdentityContext", "User");
        AssertEfMappings(initial, "FirstContext", "Box<T>", "Common", "First", "Removed");
        AssertEfMappings(initial, "SecondContext", "Box<T>", "Common", "Second");
        AssertEfMappings(initial, "GenericContext<T>.Closed", "Common");
        AssertEfMappings(initial, "FirstBase.Removable", "Removed");
        AssertEfMappings(initial, "GenericContext<T>.Open");
        AssertEfMappings(initial, "GenericContext<T>.NestedOpen");

        WriteFile("App", "Contexts.cs", contexts.Replace("ModelBuilder model) { }",
            "ModelBuilder model) { model.Ignore<Common>(); model.Ignore<Removed>(); }"));
        var incremental = await RunIncrementalIndexAsync();
        await AssertEfIncrementalParityAsync(incremental);
        AssertEfMappings(incremental, "FirstContext", "Box<T>", "First");
        AssertEfMappings(incremental, "SecondContext", "Box<T>", "Common", "Second");
        AssertEfMappings(incremental, "GenericContext<T>.Closed", "Common");
        AssertEfMappings(incremental, "FirstBase.Removable");
        var property = ResolveSymbolId(incremental, "global::App.GenericContext<T>.Closed");
        var remaining = Assert.Single(QueryEdges(incremental, "MapsTo", Provenance.FrameworkDerived),
            e => e.SourceSymbolId == property);
        using var store = OpenStore(DbPath);
        var key = (remaining.SourceSymbolId, remaining.TargetSymbolId, remaining.Kind);
        Assert.Equal(2, store.GetEdgeSites(initial, [key])[key].Count);
        var site = Assert.Single(store.GetEdgeSites(incremental, [key])[key]);
        Assert.EndsWith("Contexts.cs", site.DocumentPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EfModel_AppliedConfigurationsAndBuilderHelpersConvergeAfterBodyEdit()
    {
        const string configuration = """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;
            namespace App;
            public class RootConfig : IEntityTypeConfiguration<Root>
            {
                private RootConfig() { }
                public static RootConfig Create() => new();
                public void Configure(EntityTypeBuilder<Root> builder)
                {
                    builder.OwnsOne(root => root.Owned);
                    builder.HasQueryFilter(root => true);
                    builder.HasIndex(root => root.Navigation).IsUnique();
                }
            }
            public class NoDefault : IEntityTypeConfiguration<Nav>
            {
                public NoDefault(int arg) { }
                public void Configure(EntityTypeBuilder<Nav> builder) { }
            }
            public abstract class AbstractConfig : IEntityTypeConfiguration<Nav>
            {
                public void Configure(EntityTypeBuilder<Nav> builder) { }
            }
            public class OpenConfig<T> : IEntityTypeConfiguration<Nav>
            {
                public void Configure(EntityTypeBuilder<Nav> builder) { }
            }
            """;
        const string helpers = """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;
            namespace App;
            public class Extra;
            public class Holder<T>;
            public class Detail;
            public static class Builders
            {
                public static void Add<T>(ModelBuilder model) where T : class
                {
                    model.Entity<T>();
                    model.Entity<Holder<T>>();
                    Add<T>(model);
                }

                public static void AddDetail<T>(EntityTypeBuilder<T> entity) where T : class
                {
                    entity.OwnsOne<Detail>("Detail");
                }
            }
            """;
        CreateProject("Helpers", new Dictionary<string, string> { ["Builders.cs"] = helpers },
            ["Microsoft.EntityFrameworkCore@10.0.13"]);
        CreateProject("App", new Dictionary<string, string>
        {
            ["Config.cs"] = configuration,
            ["Unrelated.cs"] = "namespace App; public class Unrelated;",
            ["Entities.cs"] = """
                namespace App;
                public class Root
                {
                    public Nav Navigation { get; set; } = null!;
                    public Owned Owned { get; set; } = null!;
                }
                public class Nav;
                public class Owned;
                """,
            ["Context.cs"] = """
                using System.Reflection;
                using Microsoft.EntityFrameworkCore;
                namespace App;
                public class Scanning : DbContext
                {
                    protected override void OnModelCreating(ModelBuilder model)
                    {
                        model.ApplyConfigurationsFromAssembly(typeof(RootConfig).Assembly);
                        Builders.Add<Extra>(model);
                        Builders.AddDetail(model.Entity<Root>());
                    }
                }
                public class Single : DbContext
                {
                    protected override void OnModelCreating(ModelBuilder model)
                    {
                        model.ApplyConfiguration(RootConfig.Create());
                        model.Entity<Root>().HasQueryFilter((System.Linq.Expressions.Expression<System.Func<Root, bool>>?)null);
                    }
                }
                public class Uncertain : DbContext
                {
                    protected override void OnModelCreating(ModelBuilder model)
                    {
                        model.ApplyConfigurationsFromAssembly(typeof(RootConfig).Assembly, _ => true);
                        model.ApplyConfigurationsFromAssembly(Assembly.GetCallingAssembly());
                    }
                }
                """,
            ["ScanOnlyContexts.cs"] = """
                using System.Reflection;
                using Microsoft.EntityFrameworkCore;
                namespace App;
                public class Executing : DbContext
                {
                    protected override void OnModelCreating(ModelBuilder model) =>
                        model.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
                }
                public class RuntimeType : DbContext
                {
                    protected override void OnModelCreating(ModelBuilder model) =>
                        model.ApplyConfigurationsFromAssembly(GetType().Assembly, predicate: null);
                }
                """
        }, ["Microsoft.EntityFrameworkCore@10.0.13"], projectReferences: ["Helpers"]);

        RestoreSolution();
        var initial = await RunFullIndexAsync(DbPath);
        AssertEfMappings(initial, "RootConfig", "Owned", "Root");
        AssertEfMappings(initial, "NoDefault", "Nav");
        AssertEfMappings(initial, "OpenConfig<T>", "Nav");
        AssertEfMappings(initial, "Scanning", "Detail", "Extra", "Holder<T>", "Nav", "Owned", "Root");
        foreach (var name in new[] { "Executing", "RuntimeType", "Single" })
            AssertEfMappings(initial, name, "Nav", "Owned", "Root");
        AssertEfMappings(initial, "Uncertain");

        var configId = ResolveSymbolId(initial, "global::App.RootConfig");
        var refs = QueryEdges(initial, "References", Provenance.FrameworkDerived)
            .Where(e => e.ExtractorVersion == new EfCoreAdapter().Version);
        string[] applyingContexts = ["Executing", "RuntimeType", "Scanning", "Single"];
        Assert.Equal(
            applyingContexts.Select(name => ResolveSymbolId(initial, "global::App." + name)).Order(StringComparer.Ordinal),
            refs.Select(e =>
            {
                Assert.Equal(configId, e.TargetSymbolId);
                var expectedDocument = e.SourceSymbolId == ResolveSymbolId(initial, "global::App.Executing") ||
                                       e.SourceSymbolId == ResolveSymbolId(initial, "global::App.RuntimeType")
                    ? "ScanOnlyContexts.cs" : "Context.cs";
                Assert.EndsWith(expectedDocument, e.SourceDocumentPath, StringComparison.Ordinal);
                return e.SourceSymbolId;
            }).Order(StringComparer.Ordinal));

        using (var store = OpenStore(DbPath))
        {
            var annotations = store.GetAnnotations(initial).Where(a =>
                a.Kind is "ef_query_filter_constraint" or "ef_unique_index_constraint").ToList();
            Assert.Equal(2, annotations.Count);
            Assert.All(annotations, a => Assert.EndsWith("Config.cs", a.DocumentPath, StringComparison.Ordinal));
        }

        WriteFile("App", "Config.cs", configuration.Replace("builder.OwnsOne",
            "builder.Ignore(root => root.Navigation);\n        builder.OwnsOne"));
        var incremental = await RunIncrementalIndexAsync();
        await AssertEfIncrementalParityAsync(incremental);
        using (var store = OpenStore(DbPath))
        {
            var timings = store.GetTimings(incremental);
            Assert.Contains(timings, timing => timing.StepName == SnapshotTimingSteps.ClosureNarrowed);
            Assert.DoesNotContain(timings, timing => timing.StepName == SnapshotTimingSteps.ClosureWidened);
        }

        AssertEfMappings(incremental, "Scanning", "Detail", "Extra", "Holder<T>", "Owned", "Root");
        foreach (var name in new[] { "Executing", "RuntimeType", "Single" })
            AssertEfMappings(incremental, name, "Owned", "Root");

        WriteFile("Helpers", "Builders.cs", helpers.Replace("model.Entity<T>();", "", StringComparison.Ordinal));
        var afterHelperEdit = await RunIncrementalIndexAsync();
        await AssertEfIncrementalParityAsync(afterHelperEdit);
        AssertEfMappings(afterHelperEdit, "Scanning", "Detail", "Holder<T>", "Owned", "Root");
    }

    [Fact]
    public async Task EfModel_AssemblyScanRequiresAReflectableParameterlessConstructor()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["Model.cs"] = """
                using Microsoft.EntityFrameworkCore;
                using Microsoft.EntityFrameworkCore.Metadata.Builders;
                namespace App;
                public class Included;
                public class Excluded;
                public struct ExplicitConstructor : IEntityTypeConfiguration<Included>
                {
                    public ExplicitConstructor() { }
                    public void Configure(EntityTypeBuilder<Included> builder) { }
                }
                public struct ImplicitConstructor : IEntityTypeConfiguration<Excluded>
                {
                    public void Configure(EntityTypeBuilder<Excluded> builder) { }
                }
                public class ScanningContext : DbContext
                {
                    protected override void OnModelCreating(ModelBuilder model) =>
                        model.ApplyConfigurationsFromAssembly(GetType().Assembly);
                }
                public class DirectContext : DbContext
                {
                    protected override void OnModelCreating(ModelBuilder model) =>
                        model.ApplyConfiguration(new ImplicitConstructor());
                }
                """
        }, ["Microsoft.EntityFrameworkCore@10.0.13"]);

        RestoreSolution();
        var snapshot = await RunFullIndexAsync(DbPath);
        AssertEfMappings(snapshot, "ScanningContext", "Included");
        AssertEfMappings(snapshot, "DirectContext", "Excluded");
        AssertEfMappings(snapshot, "ExplicitConstructor", "Included");
        AssertEfMappings(snapshot, "ImplicitConstructor", "Excluded");
    }

    [Fact]
    public async Task EfModel_QueryFiltersUseTheLastCallPerRootEntityAndKey()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["Model.cs"] = """
                using System;
                using System.Linq.Expressions;
                using Microsoft.EntityFrameworkCore;
                namespace App;
                public class Direct;
                public class Named;
                public class Chained;
                public class Retained;
                public abstract class BaseContext : DbContext
                {
                    protected override void OnModelCreating(ModelBuilder model) =>
                        model.Entity<Named>().HasQueryFilter("ACL", x => true);
                }
                public class Context : BaseContext
                {
                    protected override void OnModelCreating(ModelBuilder model)
                    {
                        base.OnModelCreating(model);
                        model.Entity<Direct>().HasQueryFilter((Expression<Func<Direct, bool>>?)null);
                        model.Entity<Named>().HasQueryFilter("ACL", default(Expression<Func<Named, bool>>));
                        model.Entity<Chained>().HasQueryFilter(x => true)
                            .HasQueryFilter((Expression<Func<Chained, bool>>?)null);
                        model.Entity<Retained>().HasQueryFilter("B", x => true)
                            .HasQueryFilter("A", x => false).HasQueryFilter("B", x => false);
                    }
                }
                """
        }, ["Microsoft.EntityFrameworkCore@10.0.13"]);

        RestoreSolution();
        var snapshot = await RunFullIndexAsync(DbPath);
        using var store = OpenStore(DbPath);
        var retained = ResolveSymbolId(snapshot, "global::App.Retained");
        string[] expected = ["Retained: HasQueryFilter A: x => false", "Retained: HasQueryFilter B: x => false"];
        Assert.Equal(
            expected,
            store.GetAnnotations(snapshot).Where(a => a.Kind == "ef_query_filter_constraint").Select(a =>
            {
                Assert.Equal(retained, a.SymbolId);
                return a.Value;
            }).Order(StringComparer.Ordinal));
    }

    private void AssertEfMappings(string snapshot, string source, params string[] targets)
    {
        var sourceId = ResolveSymbolId(snapshot, "global::App." + source);
        Assert.Equal(
            targets.Select(target => ResolveSymbolId(snapshot, "global::App." + target)).Order(StringComparer.Ordinal),
            QueryEdges(snapshot, "MapsTo", Provenance.FrameworkDerived).Where(e => e.SourceSymbolId == sourceId)
                .Select(e => e.TargetSymbolId).Order(StringComparer.Ordinal));
    }

    private async Task AssertEfIncrementalParityAsync(string incremental)
    {
        var cleanDb = DbPath + "." + incremental + ".clean";
        var full = await RunFullIndexAsync(cleanDb);
        SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, incremental, cleanDb, full);
    }
}
