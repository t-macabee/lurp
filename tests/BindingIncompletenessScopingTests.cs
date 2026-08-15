using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Tests;

/// <summary>
/// Unit coverage for the R6 lockstep filter that scopes the incremental
/// binding-incompleteness save to the deletion set. The end-to-end null-path
/// bucket (doc-less <c>extractor_failure</c> aggregates) cannot be reproduced
/// deterministically from source — it requires an extractor stage to throw —
/// so the null-bucket carry-forward guarantee is pinned here at the predicate.
/// </summary>
public sealed class BindingIncompletenessScopingTests
{
    private static BindingIncompletenessRecord Row(string? documentPath, string reason, int count)
        => new("App", documentPath, reason, count, "v-test");

    [Fact]
    public void ScopeBindingIncompleteness_ExcludesNullPathBucket_ForCarryForward()
    {
        // An affected project re-extracted with a document-scoped pass may emit
        // a PARTIAL doc-less aggregate (document_path = null/""). Including it in
        // the scoped save overwrites the copied-forward full count via the
        // ON CONFLICT upsert, undercounting the null bucket. The lockstep policy
        // (TRUST_KERNEL R6, EXCLUDE-AND-CARRY-FORWARD) requires the save to drop
        // null/empty-path records so their carried-forward value survives.
        var scope = new HashSet<string>(System.StringComparer.Ordinal) { "App/Helper.cs" };
        var records = new[]
        {
            Row("App/Helper.cs", "filtered_external", 3),   // in scope — kept
            Row("App/External.cs", "filtered_external", 5), // out of scope — dropped
            Row(null, "extractor_failure", 7),              // null bucket — must be dropped (carry-forward)
            Row("", "extractor_failure", 2),                // empty bucket — must be dropped (carry-forward)
        };

        var scoped = IncrementalIndexer.ScopeBindingIncompleteness(records, scope);

        Assert.Single(scoped);
        Assert.Equal("App/Helper.cs", scoped[0].DocumentPath);
        Assert.DoesNotContain(scoped, r => string.IsNullOrEmpty(r.DocumentPath));
    }
}
