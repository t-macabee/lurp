using System.Text.Json;
using Lurp.Handlers;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Storage.Tests;

/// <summary>
/// PR-7 §3.6 surfaces: impact grouping and path continuation, capsule single-tier
/// continuation, and the status document-manifest summary.
///
/// The property these share is that truncation must be both honest and *actionable*:
/// an output that admits it dropped something and offers no way to fetch it forces the
/// consumer to re-read everything with a wider bound.
/// </summary>
public sealed class OutputContinuationTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lurp_pr7_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    // ---- SequenceCursor -----------------------------------------------------

    [Fact]
    public void SequenceCursor_RoundTrips_AndRejectsACursorFromADifferentRequest()
    {
        var cursor = new SequenceCursor("snap-1", SequenceCursor.ComputeFingerprint("M:A", "Downstream"), "impact", 25);

        var decoded = SequenceCursor.TryDecode(cursor.Encode());

        Assert.NotNull(decoded);
        Assert.Equal(cursor, decoded);
        decoded!.Validate("snap-1", SequenceCursor.ComputeFingerprint("M:A", "Downstream"), "impact");

        // Each of the three bindings must fail loudly rather than resume an offset
        // against a sequence the cursor was never issued for.
        Assert.Throws<ArgumentException>(() => decoded.Validate("snap-2", SequenceCursor.ComputeFingerprint("M:A", "Downstream"), "impact"));
        Assert.Throws<ArgumentException>(() => decoded.Validate("snap-1", SequenceCursor.ComputeFingerprint("M:B", "Downstream"), "impact"));
        Assert.Throws<ArgumentException>(() => decoded.Validate("snap-1", SequenceCursor.ComputeFingerprint("M:A", "Downstream"), "capsule-tier"));
    }

    [Fact]
    public void SequenceCursor_TryDecode_OnGarbage_ReturnsNullInsteadOfThrowing()
    {
        Assert.Null(SequenceCursor.TryDecode("not-base64!!"));
        Assert.Null(SequenceCursor.TryDecode(Convert.ToBase64String("not json"u8.ToArray())));
    }

    // ---- impact: grouping, capping, continuation ----------------------------

    private const string ImpactSnapshot = "snap-pr7-impact";

    /// <summary>
    /// Four maximal downstream paths from two distinct first hops:
    /// A→B→{B1,B2,B3} and A→C→C1.
    /// </summary>
    private string CreateImpactDatabase()
    {
        var dir = NewTempDir();
        using (var store = new SqliteIndexStore(Path.Combine(dir, "index.db")))
        {
            store.Open();
            store.RunMigrations();
            store.SaveEdges(ImpactSnapshot,
            [
                ImpactEdge("M:A|asm", "M:B|asm"),
                ImpactEdge("M:B|asm", "M:B1|asm"),
                ImpactEdge("M:B|asm", "M:B2|asm"),
                ImpactEdge("M:B|asm", "M:B3|asm"),
                ImpactEdge("M:A|asm", "M:C|asm"),
                ImpactEdge("M:C|asm", "M:C1|asm"),
            ]);
            store.Close();
        }
        SqliteConnection.ClearAllPools();
        return dir;
    }

    private static EdgeRecord ImpactEdge(string source, string target) => new()
    {
        SourceSymbolId = source,
        TargetSymbolId = target,
        Kind = EdgeKind.Calls.ToString(),
        Provenance = "compiler_proved",
        SnapshotId = ImpactSnapshot,
        ExtractorVersion = "v1",
    };

    private static JsonDocument RunImpact(string outputDir, params string[] extraArgs)
    {
        string[] args =
        [
            $"--output-dir={outputDir}",
            "--symbol=M:A|asm",
            "--direction=downstream",
            $"--snapshot={ImpactSnapshot}",
            "--freshness=off",
            "--quiet",
            .. extraArgs,
        ];

        var originalOut = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            Lurp.Handlers.ImpactHandler.Run(args);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return JsonDocument.Parse(captured.ToString());
    }

    private static string RunImpactSummary(string outputDir, string symbolId, string direction)
    {
        string[] args =
        [
            $"--output-dir={outputDir}",
            $"--symbol={symbolId}",
            $"--direction={direction}",
            $"--snapshot={ImpactSnapshot}",
            "--freshness=off",
            "--quiet",
            "--output=summary",
        ];

        var originalOut = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            ImpactHandler.Run(args);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return captured.ToString();
    }

    [Fact]
    public void Impact_GroupsPathsByFirstHop_OverEveryPathNotJustTheReturnedPage()
    {
        var dir = CreateImpactDatabase();

        // A page of one path, so grouping can only be complete if it was computed
        // before the page was cut : which is the whole point of the group summary.
        using var document = RunImpact(dir, "--max-paths=1");
        var root = document.RootElement;

        Assert.Equal(4, root.GetProperty("path_count_total").GetInt32());
        Assert.Equal(1, root.GetProperty("paths").GetArrayLength());

        var groups = root.GetProperty("groups");
        Assert.Equal(2, groups.GetArrayLength());
        Assert.Equal("M:B|asm", groups[0].GetProperty("first_hop_target_symbol_id").GetString());
        Assert.Equal(3, groups[0].GetProperty("path_count").GetInt32());
        Assert.Equal("M:C|asm", groups[1].GetProperty("first_hop_target_symbol_id").GetString());
        Assert.Equal(1, groups[1].GetProperty("path_count").GetInt32());
        Assert.All(
            groups.EnumerateArray(),
            group => Assert.Equal("M:A|asm", group.GetProperty("first_hop_source_symbol_id").GetString()));
    }

    [Fact]
    public void Impact_MaxPaths_TruncatesWithAnExactRemainderAndAnActionableCursor()
    {
        var dir = CreateImpactDatabase();

        using var document = RunImpact(dir, "--max-paths=3");
        var truncated = document.RootElement.GetProperty("truncated");

        Assert.Equal("max_paths", truncated.GetProperty("reason").GetString());
        Assert.Equal(3, truncated.GetProperty("returned").GetInt32());
        Assert.Equal(4, truncated.GetProperty("total").GetInt32());
        Assert.Equal(1, truncated.GetProperty("remaining").GetInt32());
        Assert.False(string.IsNullOrEmpty(truncated.GetProperty("cursor").GetString()));
    }

    [Fact]
    public void Impact_PagingWithACursor_YieldsEveryPathOnceWithNoGapsOrDuplicates()
    {
        var dir = CreateImpactDatabase();

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            using var document = cursor == null
                ? RunImpact(dir, "--max-paths=2")
                : RunImpact(dir, "--max-paths=2", $"--cursor={cursor}");

            foreach (var path in document.RootElement.GetProperty("paths").EnumerateArray())
                seen.Add(string.Join(">", path.GetProperty("hops").EnumerateArray()
                    .Select(hop => hop.GetProperty("target_symbol_id").GetString())));

            cursor = document.RootElement.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.Object
                ? truncated.GetProperty("cursor").GetString()
                : null;
            pages++;
        }
        while (cursor != null && pages < 10);

        Assert.Equal(2, pages);
        Assert.Equal(4, seen.Count);
        Assert.Equal(4, seen.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("M:B|asm>M:B1|asm", seen);
        Assert.Contains("M:B|asm>M:B2|asm", seen);
        Assert.Contains("M:B|asm>M:B3|asm", seen);
        Assert.Contains("M:C|asm>M:C1|asm", seen);
    }

    [Fact]
    public void ImpactSummary_LabelsBothEndsOfAnUpstreamFirstHop()
    {
        var dir = CreateImpactDatabase();

        var summary = RunImpactSummary(dir, "M:B1|asm", "upstream");

        Assert.Contains("M:B|asm → M:B1|asm [Calls]", summary, StringComparison.Ordinal);
    }

    // ---- search: actionable summary -----------------------------------------

    private string CreateSearchDatabase()
    {
        var dir = NewTempDir();
        var snapshotId = "snap-pr7-search";
        using var store = new SqliteIndexStore(Path.Combine(dir, "index.db"));
        store.Open();
        store.RunMigrations();

        var workspace = WorkspaceId.Create(dir, Path.Combine(dir, "Sample.slnx"));
        store.SaveSnapshot(new SnapshotRow
        {
            SnapshotId = snapshotId,
            WorkspaceId = workspace.Value,
            GitRoot = workspace.GitRoot,
            SolutionPath = workspace.SolutionPath,
            CreatedAtUtc = DateTime.UtcNow,
            Documents =
            [
                new DocumentVersion(System.Text.Encoding.UTF8.GetBytes("namespace Example; public class SearchTarget { }"))
                {
                    DocumentId = "doc-pr7-search",
                    FilePath = "src/SearchTarget.cs",
                    ContentHash = "hash-pr7-search",
                    Encoding = "utf-8",
                    LineStart = "[0]",
                    LineStarts = "[0]",
                    CreatedAtUtc = DateTime.UtcNow,
                },
            ],
        });
        store.SaveDeclarations(snapshotId,
        [
            new SymbolDeclaration
            {
                SymbolId = new SymbolId("T:Example.SearchTarget", "asm", "Example.SearchTarget"),
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "doc-pr7-search:hash-pr7-search",
                FullSpan = new DeclarationSpan(0, 48),
                SignatureSpan = new DeclarationSpan(0, 48),
                BodySpan = new DeclarationSpan(null, null),
                NameSpan = new DeclarationSpan(32, 44),
            },
        ]);
        store.BuildSearchIndex(snapshotId);
        store.MarkSnapshotComplete(snapshotId);
        store.Close();
        return dir;
    }

    private static string RunSearchSummary(string outputDir)
    {
        var originalOut = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            SearchHandler.Run(
            [
                "--query=SearchTarget",
                "--type=symbol",
                $"--output-dir={outputDir}",
                "--output=summary",
                "--freshness=off",
                "--quiet",
            ]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return captured.ToString();
    }

    [Fact]
    public void SearchSummary_IncludesTheSymbolIdForFollowUpQueries()
    {
        var dir = CreateSearchDatabase();

        var summary = RunSearchSummary(dir);

        Assert.Contains("T:Example.SearchTarget|asm", summary, StringComparison.Ordinal);
        Assert.Contains("Example.SearchTarget", summary, StringComparison.Ordinal);
    }

    // ---- capsule: single-tier continuation ----------------------------------

    private const string TierSnapshot = "snap-pr7-tier";

    /// <summary>
    /// One anchor with five direct callers : enough to page, and enough that the
    /// pages must partition the tier rather than repeat its head.
    /// </summary>
    private SqliteIndexStore CreateTierStore()
    {
        var dir = NewTempDir();
        var dbPath = Path.Combine(dir, "index.db");

        using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            var seed = new SqliteIndexStore(dbPath);
            seed.Open();
            seed.RunMigrations();
            seed.Close();
            seed.Dispose();
            SqliteConnection.ClearAllPools();

            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO workspaces (workspace_id, git_root, solution_path)
                VALUES ('ws-pr7-tier', '/fake/root', 'test.sln');
                INSERT OR IGNORE INTO snapshots (snapshot_id, workspace_id, built_at_utc)
                VALUES (@sid, 'ws-pr7-tier', '2026-01-01T00:00:00Z');
                INSERT OR IGNORE INTO documents (document_id, relative_path)
                VALUES ('doc-pr7-tier', 'test.cs');
                INSERT OR IGNORE INTO document_versions (document_version_id, document_id, content_hash)
                VALUES ('doc-v-pr7-tier', 'doc-pr7-tier', 'hash');
            ";
            cmd.Parameters.AddWithValue("@sid", TierSnapshot);
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var store = new SqliteIndexStore(dbPath);
        store.Open();

        var symbols = new List<string> { "M:MyApp.Target.Run|prod" };
        var edges = new List<EdgeRecord>();
        for (var i = 1; i <= 5; i++)
        {
            var caller = $"M:MyApp.Caller{i}.Invoke|prod";
            symbols.Add(caller);
            edges.Add(new EdgeRecord
            {
                SourceSymbolId = caller,
                TargetSymbolId = "M:MyApp.Target.Run|prod",
                Kind = EdgeKind.Calls.ToString(),
                Provenance = "compiler_proved",
                SnapshotId = TierSnapshot,
                ExtractorVersion = "v1",
            });
        }

        store.SaveDeclarations(TierSnapshot, symbols.Select(static symbolId => new SymbolDeclaration
        {
            SymbolId = SymbolId.Parse(symbolId),
            Kind = IndexedSymbolKind.Method,
            DocumentVersionId = "doc-v-pr7-tier",
            FullSpan = new DeclarationSpan(null, null),
            SignatureSpan = new DeclarationSpan(null, null),
            BodySpan = new DeclarationSpan(null, null),
            NameSpan = new DeclarationSpan(null, null),
            MetadataJson = """{"accessibility":"Public"}""",
        }));
        store.SaveEdges(TierSnapshot, edges);
        return store;
    }

    [Fact]
    public void CapsuleTierPage_PagesOneTierOnItsOwn_PartitioningItAcrossPages()
    {
        using var store = CreateTierStore();
        var anchor = SymbolId.Parse("M:MyApp.Target.Run|prod");

        var first = ContextAssembler.BuildTierPage(store, store, TierSnapshot, anchor, "directCallers",
            maxHops: 3, includeGenerated: false, offset: 0, limit: 2);
        var second = ContextAssembler.BuildTierPage(store, store, TierSnapshot, anchor, "directCallers",
            maxHops: 3, includeGenerated: false, offset: 2, limit: 2);
        var third = ContextAssembler.BuildTierPage(store, store, TierSnapshot, anchor, "directCallers",
            maxHops: 3, includeGenerated: false, offset: 4, limit: 2);

        Assert.Equal(5, first.TotalItems);
        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.Equal(2, second.Items.Count);
        Assert.True(second.HasMore);
        Assert.Single(third.Items);
        Assert.False(third.HasMore);

        var paged = first.Items.Concat(second.Items).Concat(third.Items)
            .Select(static item => item.SymbolId).ToList();
        Assert.Equal(5, paged.Distinct(StringComparer.Ordinal).Count());

        // The page is the same evidence the capsule would have carried for this tier.
        var whole = ContextAssembler.BuildTierPage(store, store, TierSnapshot, anchor, "directCallers",
            maxHops: 3, includeGenerated: false, offset: 0, limit: 100);
        Assert.Equal(whole.Items.Select(static item => item.SymbolId), paged);
        store.Close();
    }

    [Fact]
    public void CapsuleTierPage_UnknownTierName_IsRejectedRatherThanReturningEmpty()
    {
        using var store = CreateTierStore();

        var error = Assert.Throws<ArgumentException>(() => ContextAssembler.BuildTierPage(
            store, store, TierSnapshot, SymbolId.Parse("M:MyApp.Target.Run|prod"), "notATier",
            maxHops: 3, includeGenerated: false, offset: 0, limit: 10));

        Assert.Contains("directCallers", error.Message, StringComparison.Ordinal);
        store.Close();
    }

    [Fact]
    public void Capsule_StatesHowToContinueABudgetExhaustedTier()
    {
        using var store = CreateTierStore();

        var capsule = new ContextAssembler
        {
            EdgeStore = store,
            DeclarationStore = store,
            BindingIncompletenessStore = store,
            SnapshotId = TierSnapshot,
            SymbolId = SymbolId.Parse("M:MyApp.Target.Run|prod"),
            Intent = ContextIntent.Inspect,
            Budget = 100_000,
            MaxHops = 3,
        }.Assemble();

        // The omission channel is only honest if it is actionable; the capsule must
        // carry the continuation instruction, not just the admission.
        var reason = capsule.InclusionReasons["omittedTiers.budget_exhausted"];
        Assert.Contains("--tier=", reason, StringComparison.Ordinal);
        Assert.Contains("--cursor=", reason, StringComparison.Ordinal);
        store.Close();
    }

    // ---- status: document manifest summary ----------------------------------

    private string CreateStatusDatabase()
    {
        var dir = NewTempDir();
        using (var store = new SqliteIndexStore(Path.Combine(dir, "index.db")))
        {
            store.Open();
            store.RunMigrations();

            var workspaceId = WorkspaceId.Create(dir, Path.Combine(dir, "Sample.slnx"));
            var snapshotId = SnapshotId.New().ToString();
            store.SaveSnapshot(new SnapshotRow
            {
                SnapshotId = snapshotId,
                WorkspaceId = workspaceId.Value,
                GitRoot = workspaceId.GitRoot,
                SolutionPath = workspaceId.SolutionPath,
                SdkVersion = "10.0.100",
                CompilerVersion = "5.6.0",
                CreatedAtUtc = DateTime.UtcNow,
                DatabaseSchemaVersion = VersionConstants.DatabaseSchemaVersion,
                OutputSchemaVersion = VersionConstants.OutputSchemaVersion,
                ExtractorVersion = "extractor-test",
                ToolVersion = "tool-test",
                Documents =
                [
                    StatusDocument("src/One.cs", "hash-1"),
                    StatusDocument("src/Two.cs", "hash-2"),
                    StatusDocument("src/Three.cs", "hash-3"),
                ],
                Projects = [new ProjectRow { Name = "App", TargetFramework = "net10.0" }],
            });
            store.MarkSnapshotComplete(snapshotId);
            store.Close();
        }
        SqliteConnection.ClearAllPools();
        return dir;
    }

    private static DocumentVersion StatusDocument(string path, string hash) => new([])
    {
        DocumentId = path,
        FilePath = path,
        ContentHash = hash,
        Encoding = "utf-8",
        LineStart = "",
        LineStarts = "",
        CreatedAtUtc = DateTime.UtcNow,
    };

    private static async Task<JsonDocument> RunStatus(string outputDir, params string[] extraArgs)
    {
        string[] args = [$"--output-dir={outputDir}", "--json", .. extraArgs];

        var originalOut = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            await Lurp.Handlers.StatusHandler.Run(args);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return JsonDocument.Parse(captured.ToString());
    }

    private static async Task<string> RunStatusRaw(string outputDir, params string[] extraArgs)
    {
        string[] args = [$"--output-dir={outputDir}", .. extraArgs];

        var originalOut = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            await Lurp.Handlers.StatusHandler.Run(args);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return captured.ToString();
    }

    [Fact]
    public async Task StatusJson_SummarizesTheDocumentManifestByDefault()
    {
        var dir = CreateStatusDatabase();

        using var document = await RunStatus(dir);
        var manifest = document.RootElement.GetProperty("manifest");

        // Summarized, not silently dropped: a reader must be able to tell three
        // documents from none without asking for the detail.
        Assert.False(manifest.TryGetProperty("documentVersions", out _));
        Assert.Equal(3, manifest.GetProperty("documentCount").GetInt32());
        Assert.Contains("--detail=documents", manifest.GetProperty("documentsNote").GetString()!, StringComparison.Ordinal);

        // The summary must not cost any other manifest field.
        Assert.Equal("extractor-test", manifest.GetProperty("extractorVersion").GetString());
        Assert.Equal("net10.0", manifest.GetProperty("targetFrameworks").GetProperty("App").GetString());
    }

    [Fact]
    public async Task StatusJson_DetailDocuments_RestoresThePerDocumentVersionMap()
    {
        var dir = CreateStatusDatabase();

        using var document = await RunStatus(dir, "--detail=documents");
        var manifest = document.RootElement.GetProperty("manifest");

        var versions = manifest.GetProperty("documentVersions");
        Assert.Equal(3, versions.EnumerateObject().Count());
        Assert.False(manifest.TryGetProperty("documentCount", out _));
    }

    /// <summary>
    /// Status historically required the bare <c>--json</c> flag while every other read
    /// command accepts <c>--output=</c>; this locks in the added <c>--output=json</c> alias
    /// producing the same manifest, and confirms <c>--json</c> still works unchanged.
    /// </summary>
    [Fact]
    public async Task StatusOutputJson_MatchesTheLegacyJsonFlag()
    {
        var dir = CreateStatusDatabase();

        var viaOutputFlag = await RunStatusRaw(dir, "--output=json");
        var viaLegacyFlag = await RunStatusRaw(dir, "--json");

        using var outputDoc = JsonDocument.Parse(viaOutputFlag);
        using var legacyDoc = JsonDocument.Parse(viaLegacyFlag);
        Assert.Equal(
            legacyDoc.RootElement.GetProperty("manifest").GetProperty("extractorVersion").GetString(),
            outputDoc.RootElement.GetProperty("manifest").GetProperty("extractorVersion").GetString());
    }

    /// <summary>
    /// Neither flag and <c>--output=summary</c> must both fall through to the historical
    /// human-readable text, not JSON: the default must not silently change shape for
    /// existing callers that never pass either flag.
    /// </summary>
    [Fact]
    public async Task StatusOutputSummary_AndNoFlag_BothStayHumanReadable()
    {
        var dir = CreateStatusDatabase();

        var noFlag = await RunStatusRaw(dir);
        var viaOutputFlag = await RunStatusRaw(dir, "--output=summary");

        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(noFlag));
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(viaOutputFlag));
    }
}
