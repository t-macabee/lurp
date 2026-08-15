using Microsoft.Build.Locator;

namespace Lurp.Tests;

public sealed class MultiCycleConvergenceTests : IntegrationTestBase
{
    private readonly string _cleanRebuildDbPath;

    public MultiCycleConvergenceTests()
    {
        _cleanRebuildDbPath = Path.Combine(TestDir, "clean.db");
    }

    private async Task RunMultiCycleParityAsync(Action seed, params Action[] mutations)
    {
        seed();
        var snapshotA = await RunFullIndexAsync(DbPath);

        string snapshotB = snapshotA;
        foreach (var mutation in mutations)
        {
            mutation();
            snapshotB = await RunIncrementalIndexAsync();
        }

        var snapshotC = await RunFullIndexAsync(_cleanRebuildDbPath);

        SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, snapshotB, _cleanRebuildDbPath, snapshotC);
        Assert.NotEqual(snapshotA, snapshotB);
    }

    [SkippableFact]
    public async Task Incremental_FiveSequentialCycles_ConvergesWithCleanRebuild()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        // Mutation 1: add a new service.
        Action addService = () => WriteFile("Services", "ProductService.cs", """
            using Core;

            namespace Services;

            public class Product { }
            public class ProductDto { }
            public class ProductSearch { }

            public class ProductService : BaseReadService<Product, ProductDto, ProductSearch>
            {
            }
            """);

        // Mutation 2: rename a method (Find → Search across Core).
        Action renameMethod = () =>
        {
            WriteFile("Core", "IBaseReadService.cs", """
                namespace Core;

                public interface IBaseReadService<TResponse, TSearch>
                {
                    TResponse Search(TSearch search);
                }
                """);
            WriteFile("Core", "BaseReadService.cs", """
                namespace Core;

                public class BaseReadService<TEntity, TResponse, TSearch> : IBaseReadService<TResponse, TSearch>
                {
                    public virtual TResponse Search(TSearch search) => default!;
                }
                """);
            WriteFile("App", "Consumer.cs", """
                using Core;
                using Services;

                namespace App;

                public class Consumer
                {
                    public void UseServices()
                    {
                        var userSvc = new UserService();
                        userSvc.Search(new UserSearch());

                        var orderSvc = new OrderService();
                        orderSvc.Search(new OrderSearch());

                        var reportSvc = new ReportingService();
                        reportSvc.Search(new ReportSearch());
                        reportSvc.Echo(42);
                        reportSvc.Format(new ReportDto());
                    }
                }
                """);
        };

        // Mutation 3: change a base class (make it abstract).
        Action changeBaseClass = () => WriteFile("Core", "BaseReadService.cs", """
            namespace Core;

            public abstract class BaseReadService<TEntity, TResponse, TSearch> : IBaseReadService<TResponse, TSearch>
            {
                public virtual TResponse Search(TSearch search) => default!;
            }
            """);

        // Mutation 4: split UserService into two partial files.
        Action splitFile = () =>
        {
            DeleteFile("Services", "UserService.cs");
            WriteFile("Services", "UserService.Part1.cs", """
                using Core;

                namespace Services;

                public class User { }
                public class UserDto { }
                public class UserSearch { }

                public partial class UserService : BaseReadService<User, UserDto, UserSearch>
                {
                }
                """);
            WriteFile("Services", "UserService.Part2.cs", """
                using Core;

                namespace Services;

                public partial class UserService
                {
                    public string Display(UserDto dto) => dto.ToString() ?? "";
                }
                """);
        };

        // Mutation 5: revert the rename (Search → Find).
        Action revertRename = () =>
        {
            WriteFile("Core", "IBaseReadService.cs", """
                namespace Core;

                public interface IBaseReadService<TResponse, TSearch>
                {
                    TResponse Find(TSearch search);
                }
                """);
            WriteFile("Core", "BaseReadService.cs", """
                namespace Core;

                public abstract class BaseReadService<TEntity, TResponse, TSearch> : IBaseReadService<TResponse, TSearch>
                {
                    public virtual TResponse Find(TSearch search) => default!;
                }
                """);
            WriteFile("App", "Consumer.cs", """
                using Core;
                using Services;

                namespace App;

                public class Consumer
                {
                    public void UseServices()
                    {
                        var userSvc = new UserService();
                        userSvc.Find(new UserSearch());

                        var orderSvc = new OrderService();
                        orderSvc.Find(new OrderSearch());

                        var reportSvc = new ReportingService();
                        reportSvc.Find(new ReportSearch());
                        reportSvc.Echo(42);
                        reportSvc.Format(new ReportDto());
                    }
                }
                """);
        };

        await RunMultiCycleParityAsync(
            () => MultiProjectFixture.Seed(this),
            addService,
            renameMethod,
            changeBaseClass,
            splitFile,
            revertRename);
    }

    [SkippableFact]
    public async Task MoveFileBetweenProjects_ConvergesWithCleanRebuild()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("Lib",
            new Dictionary<string, string>
            {
                ["Lib.cs"] = """
                    namespace Lib;

                    public class Lib
                    {
                        public void Foo() { }
                        public void Bar() { }
                    }
                    """,
            });

        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Service.cs"] = """
                    namespace App;

                    public class Service
                    {
                        public void Use(Lib.Lib lib) => lib.Foo();
                    }
                    """,
            },
            projectReferences: ["Lib"]);

        var beforeSnapshotId = await RunFullIndexAsync(DbPath);

        using var storeBefore = OpenStore(DbPath);
        var beforeSymbolIds = storeBefore.GetSymbolIdsInSnapshot(beforeSnapshotId);
        var serviceFqns = new HashSet<string>();
        foreach (var id in beforeSymbolIds)
        {
            var info = storeBefore.GetSymbolInfo(id, beforeSnapshotId);
            if (info?.FullyQualifiedName?.StartsWith("global::App.Service") == true)
                serviceFqns.Add(info.FullyQualifiedName);
        }
        storeBefore.Close();
        Assert.NotEmpty(serviceFqns);

        DeleteFile("App", "Service.cs");
        WriteFile("Lib", "Service.cs", """
            namespace Lib;

            public class Service
            {
                public void Use(Lib lib) => lib.Bar();
            }
            """);

        var afterMoveSnapshotId = await RunIncrementalIndexAsync();

        using var storeAfter = OpenStore(DbPath);
        var afterSymbolIds = storeAfter.GetSymbolIdsInSnapshot(afterMoveSnapshotId);

        var afterServiceFqns = new HashSet<string>();
        foreach (var id in afterSymbolIds)
        {
            var info = storeAfter.GetSymbolInfo(id, afterMoveSnapshotId);
            if (info?.FullyQualifiedName?.StartsWith("global::Lib.Service") == true)
                afterServiceFqns.Add(info.FullyQualifiedName);
        }
        storeAfter.Close();

        foreach (var oldFqn in serviceFqns)
        {
            var newFqn = oldFqn.Replace("global::App.", "global::Lib.");
            Assert.True(afterServiceFqns.Contains(newFqn),
                $"Expected re-keyed symbol '{newFqn}' not found after move. Old: {oldFqn}");
        }

        var snapshotC = await RunFullIndexAsync(_cleanRebuildDbPath);
        SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, afterMoveSnapshotId, _cleanRebuildDbPath, snapshotC);
    }
}
