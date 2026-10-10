using System.Text.Json;
using Lurp.Shared;
using Lurp.Workspace;

namespace Lurp.Tests;

/// <summary>
///     B22: <see cref="FreshnessJson" /> is the single freshness builder. The cheap shape
///     keeps the eight keys the read-only surfaces always emitted; both <c>lurp_status</c>
///     paths emit the twelve-key shape with the same key sequence.
/// </summary>
public sealed class FreshnessJsonTests
{
    private static readonly string[] _cheapKeys =
    [
        "state",
        "method",
        "changed_document_count",
        "changed_documents_sample",
        "changed_documents_sample_truncated",
        "checked_at_utc",
        "snapshot_id",
        "scope"
    ];

    private static readonly string[] _statusKeys =
    [
        "state",
        "method",
        "scope",
        "snapshot_id",
        "checked_at_utc",
        "changed_document_count",
        "changed_documents_sample",
        "changed_documents_sample_truncated",
        "is_fresh",
        "mismatches",
        "mismatches_truncated",
        "mismatches_total"
    ];

    private static readonly string[] _rowKeys = ["kind", "description", "document", "detail"];

    private static readonly string[] _aDocuments = ["A.cs"];

    [Fact]
    public void Cheap_HasTheEightKeys_InOrder()
    {
        var stamp = Stamp("fresh", "stat", 3, ["a.cs", "b.cs", "c.cs"], "documents_only");

        var cut = ToElement(FreshnessJson.Cheap(stamp, 2));
        Assert.Equal(_cheapKeys, KeyNames(cut));
        Assert.Equal(3, cut.GetProperty("changed_document_count").GetInt32());
        Assert.Equal(2, cut.GetProperty("changed_documents_sample").GetArrayLength());
        Assert.True(cut.GetProperty("changed_documents_sample_truncated").GetBoolean());

        var whole = ToElement(FreshnessJson.Cheap(stamp, 3));
        Assert.Equal(JsonValueKind.Null, whole.GetProperty("changed_documents_sample_truncated").ValueKind);
    }

    [Theory]
    [InlineData("fresh", JsonValueKind.True)]
    [InlineData("stale", JsonValueKind.False)]
    [InlineData("unknown", JsonValueKind.Null)]
    public void ForStatus_Stamp_HasTwelveKeys_IsFreshFollowsState(string state, JsonValueKind expected)
    {
        var stamp = Stamp(state, "stat", 0, [], "documents_only");

        var element = ToElement(FreshnessJson.ForStatus(stamp, 10));
        Assert.Equal(_statusKeys, KeyNames(element));
        Assert.Equal(expected, element.GetProperty("is_fresh").ValueKind);
        Assert.Equal(JsonValueKind.Null, element.GetProperty("mismatches").ValueKind);
        Assert.Equal(JsonValueKind.Null, element.GetProperty("mismatches_truncated").ValueKind);
        Assert.Equal(JsonValueKind.Null, element.GetProperty("mismatches_total").ValueKind);
    }

    [Fact]
    public void ForStatus_StampAndFull_HaveTheSameKeySequence()
    {
        var stamp = Stamp("fresh", "stat", 0, [], "documents_only");
        var result = new WorkspaceFreshness.FreshnessResult(true, []);

        var stampKeys = KeyNames(ToElement(FreshnessJson.ForStatus(stamp, 10)));
        var fullKeys = KeyNames(ToElement(FreshnessJson.ForStatus("snap", result, DateTime.UtcNow, 10, 10, false)));

        Assert.Equal(stampKeys, fullKeys);
    }

    [Fact]
    public void ForStatus_Full_CountsDistinctDocuments()
    {
        var element = ToElement(FreshnessJson.ForStatus("snap", MixedResult(), DateTime.UtcNow, 10, 10, false));

        Assert.Equal("stale", element.GetProperty("state").GetString());
        Assert.False(element.GetProperty("is_fresh").GetBoolean());
        Assert.Equal(1, element.GetProperty("changed_document_count").GetInt32());
        Assert.Equal(_aDocuments, Sample(element));
        Assert.Equal(JsonValueKind.Null, element.GetProperty("mismatches").ValueKind);
        Assert.Equal(3, element.GetProperty("mismatches_total").GetInt32());
    }

    [Fact]
    public void ForStatus_Full_WithRows_NullsSample_AndCapsRows()
    {
        var element = ToElement(FreshnessJson.ForStatus("snap", MixedResult(), DateTime.UtcNow, 10, 1, true));

        Assert.Equal(1, element.GetProperty("mismatches").GetArrayLength());
        Assert.True(element.GetProperty("mismatches_truncated").GetBoolean());
        Assert.Equal(3, element.GetProperty("mismatches_total").GetInt32());
        Assert.Equal(JsonValueKind.Null, element.GetProperty("changed_documents_sample").ValueKind);
        Assert.Equal(1, element.GetProperty("changed_document_count").GetInt32());
    }

    [Fact]
    public void ForStatus_Full_Fresh()
    {
        var result = new WorkspaceFreshness.FreshnessResult(true, []);

        var element = ToElement(FreshnessJson.ForStatus("snap", result, DateTime.UtcNow, 10, 10, true));

        Assert.Equal("fresh", element.GetProperty("state").GetString());
        Assert.True(element.GetProperty("is_fresh").GetBoolean());
        Assert.Equal(0, element.GetProperty("changed_document_count").GetInt32());
        Assert.Equal(0, element.GetProperty("changed_documents_sample").GetArrayLength());
        Assert.Equal(0, element.GetProperty("mismatches").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, element.GetProperty("mismatches_truncated").ValueKind);
    }

    [Fact]
    public void CapMismatches_ReturnsRows_Truncated_Total()
    {
        var capped = FreshnessJson.CapMismatches(MixedResult().Mismatches, 2);

        Assert.True(capped.Truncated);
        Assert.Equal(3, capped.Total);
        Assert.Equal(2, capped.Rows.Count);

        var row = ToElement(capped.Rows[0]);
        Assert.Equal(_rowKeys, KeyNames(row));
        Assert.Equal("DocumentModified", row.GetProperty("kind").GetString());
        Assert.Equal("A.cs", row.GetProperty("document").GetString());
    }

    private static WorkspaceFreshness.FreshnessResult MixedResult()
    {
        return new WorkspaceFreshness.FreshnessResult(false,
        [
            new SnapshotMismatch(MismatchKind.DocumentModified, "modified A", new DocumentId("A.cs"), null),
            new SnapshotMismatch(MismatchKind.DocumentRemoved, "removed A", new DocumentId("A.cs"), null),
            new SnapshotMismatch(MismatchKind.SdkChanged, "sdk", null, null)
        ]);
    }

    private static FreshnessStamp Stamp(string state, string method, int count, IReadOnlyList<string> sample, string scope)
    {
        return new FreshnessStamp(state, method, count, sample, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "snap", scope);
    }

    private static string[] Sample(JsonElement element)
    {
        return element.GetProperty("changed_documents_sample").EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    private static string[] KeyNames(JsonElement element)
    {
        return element.EnumerateObject().Select(p => p.Name).ToArray();
    }

    private static JsonElement ToElement(object value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value, LurpJsonOptions.Indented));
        return doc.RootElement.Clone();
    }
}
