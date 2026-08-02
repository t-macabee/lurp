using Xunit;

// WorkspaceLoaderTests, RealSolutionIntegrationTests, and T9CompletenessTests
// redirect the process-wide Console.Out to capture output. The indexing
// pipeline (WorkspaceLoader/IndexRunner/IncrementalIndexer) writes via raw
// Console.Write/WriteLine with no locking, so under xUnit's default
// parallel-collection execution, any concurrently running test's console
// output can land in another test's capture buffer. Serializing the
// assembly removes that race entirely.
[assembly: CollectionBehavior(MaxParallelThreads = 1)]
