using Lurp.Handlers;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Tests;

public sealed class AnnotationLifecycleTests : IntegrationTestBase
{
    private const string OriginalSource = "namespace P; public class Retained { } public class Removed { }";
    private const string EditedSource = "namespace P; public class Retained { } public class Added { }";

    [Fact]
    public async Task IncrementalIndex_PrunesAnnotationsForRemovedSymbols()
    {
        var (original, retainedId, removedId) = await SeedAnnotationsAsync();
        WriteFile("P", "Types.cs", EditedSource);

        var incremental = await RunIncrementalIndexWithResultAsync();

        Assert.NotEqual(original, incremental.NewSnapshotId);
        using var store = OpenStore(DbPath);
        Assert.DoesNotContain(removedId, store.GetSymbolIdsInSnapshot(incremental.NewSnapshotId));
        Assert.Equal(["keep", "keep"], store.GetAnnotations(incremental.NewSnapshotId)
            .Where(a => a.DocumentPath is null).Select(a => a.Value));
        Assert.Contains(store.GetAnnotations(original), a => a.SymbolId == removedId && a.Value == "remove");
        Assert.Contains(retainedId, store.GetSymbolIdsInSnapshot(incremental.NewSnapshotId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullIndex_CarriesForwardOnlyUserAnnotationsForLiveSymbols(bool force)
    {
        var (original, retainedId, removedId) = await SeedAnnotationsAsync();
        if (!force)
            WriteFile("P", "Types.cs", EditedSource);

        var (rebuilt, output) = await RunIndexAsync(strategy: "full", force: force);

        Assert.Contains("full rebuild", output);
        if (force)
            Assert.Equal(original, rebuilt);
        else
            Assert.NotEqual(original, rebuilt);

        using var store = OpenStore(DbPath);
        var retained = store.GetAnnotations(rebuilt, retainedId);
        Assert.Equal(["keep", "keep"], retained.Select(a => a.Value));
        Assert.All(retained, a => Assert.Null(a.DocumentPath));
        if (force)
            Assert.Equal("remove", Assert.Single(store.GetAnnotations(rebuilt, removedId)).Value);
        else
        {
            Assert.Empty(store.GetAnnotations(rebuilt, removedId));
            Assert.Contains(store.GetAnnotations(original), a => a.SymbolId == removedId);
        }
    }

    [Fact]
    public async Task FullIndex_PreservesAnnotationsWhenReplacingAnIncrementalSnapshot()
    {
        await SeedAnnotationsAsync();
        WriteFile("P", "Types.cs", EditedSource);
        var (incremental, _) = await RunIndexAsync(strategy: "incremental");
        using (var store = OpenStore(DbPath))
        {
            var addedId = HandlerBootstrap.ResolveSymbolArg(store, "P.Added", incremental);
            store.SaveUserAnnotation(incremental, addedId, "note", "added after incremental index");
        }

        var (rebuilt, output) = await RunIndexAsync(strategy: "full");

        Assert.Equal(incremental, rebuilt);
        Assert.Contains("full rebuild", output);
        using var rebuiltStore = OpenStore(DbPath);
        var rebuiltAddedId = HandlerBootstrap.ResolveSymbolArg(rebuiltStore, "P.Added", rebuilt);
        Assert.Equal("added after incremental index", Assert.Single(rebuiltStore.GetAnnotations(rebuilt, rebuiltAddedId)).Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InterruptedFullForceRebuild_PreservesNotesThroughCleanupAndRetry(bool beforeExtraction)
    {
        var (original, retainedId, removedId) = await SeedAnnotationsAsync();
        using (var store = OpenStore(DbPath))
        {
            await Assert.ThrowsAsync<IOException>(() => IndexRunner.RunAsync(
                store, SolutionPath, [], null, "full", output: new InterruptRebuildSink(beforeExtraction), force: true));
        }

        // A different successful build cleans up incomplete payloads and superseded failures.
        WriteFile("P", "Types.cs", EditedSource);
        var (different, _) = await RunIndexAsync(strategy: "full");
        Assert.NotEqual(original, different);
        using (var store = OpenStore(DbPath))
        {
            Assert.Equal(["keep", "keep"], store.GetAnnotations(original, retainedId).Select(a => a.Value));
            store.SaveUserAnnotation(different, retainedId, "note", "only on the other snapshot");
        }

        WriteFile("P", "Types.cs", OriginalSource);
        var (retried, _) = await RunIndexAsync(strategy: "full");

        Assert.Equal(original, retried);
        using var rebuilt = OpenStore(DbPath);
        Assert.Equal(["keep", "keep"], rebuilt.GetAnnotations(retried, retainedId).Select(a => a.Value));
        Assert.Equal("remove", Assert.Single(rebuilt.GetAnnotations(retried, removedId)).Value);
    }

    private sealed class InterruptRebuildSink(bool beforeExtraction) : IOutputSink
    {
        private bool _savingManifest;

        public void Write(string message)
        {
            if (!beforeExtraction && message == "Building search index... ")
                throw new IOException("Interrupted before search index completion.");
            if (message == "Saving snapshot to database... ")
                _savingManifest = true;
        }

        public void WriteLine(string message = "")
        {
            if (beforeExtraction && _savingManifest && message == "done.")
                throw new IOException("Interrupted after manifest commit.");
        }

        public void WriteErrorLine(string message = "") { }
    }

    private async Task<(string SnapshotId, string RetainedId, string RemovedId)> SeedAnnotationsAsync()
    {
        var snapshot = await IndexFullAsync("P", new Dictionary<string, string> { ["Types.cs"] = OriginalSource });
        using var store = OpenStore(DbPath);
        var retainedId = HandlerBootstrap.ResolveSymbolArg(store, "P.Retained", snapshot);
        var removedId = HandlerBootstrap.ResolveSymbolArg(store, "P.Removed", snapshot);
        store.SaveUserAnnotation(snapshot, retainedId, "note", "keep");
        store.SaveUserAnnotation(snapshot, retainedId, "note", "keep");
        store.SaveUserAnnotation(snapshot, removedId, "note", "remove");
        store.SaveAnnotations(snapshot, [new AnnotationRecord(retainedId, "note", "document annotation", "src/P/Types.cs")]);
        return (snapshot, retainedId, removedId);
    }
}
