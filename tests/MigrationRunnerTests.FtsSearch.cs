using Lurp.Adapters;
using Lurp.Shared;
using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using System.Text;
using DocumentId = Lurp.Workspace.DocumentId;

namespace Lurp.Storage.Tests;

public partial class MigrationRunnerTests
{
    public class FtsSearchTests : IDisposable
    {
        private readonly string _dbPath;
        private SqliteIndexStore? _store;

        public FtsSearchTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_fts_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            _store?.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private SqliteIndexStore CreateStore()
        {
            _store?.Dispose();
            var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();
            _store = store;
            return store;
        }

        private static byte[] StringToBytes(string text) => Encoding.UTF8.GetBytes(text);

        private static void CreateSnapshotWithContent(
            SqliteIndexStore store, string snapshotId, string relativePath, string content)
        {
            var lineStarts = "[0]";
            var sourceBytes = StringToBytes(content);

            var manifest = new SnapshotRow
            {
                SnapshotId = snapshotId,
                WorkspaceId = "workspace:///root/proj",
                GitRoot = "/root",
                SolutionPath = "/root/proj",
                SdkVersion = "10.0.301",
                CompilerVersion = "4.12.0.0",
                CreatedAtUtc = DateTime.UtcNow,
                Documents = new List<DocumentVersion>
                {
                    new DocumentVersion(sourceBytes) { DocumentId = "doc-" + relativePath, FilePath = relativePath, ContentHash = "hash-" + relativePath, Encoding = "utf-8", LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue, LineStarts = lineStarts },
                }
            };
            store.SaveSnapshot(manifest);
        }

        [Fact]
        public void BuildSearchIndex_AfterSavingSnapshot_SearchReturnsResults()
        {
            var store = CreateStore();
            var snapshotId = "snap-fts-001";

            CreateSnapshotWithContent(store, snapshotId, "src/Program.cs",
                "using System;\nclass Program { static void Main() { Console.WriteLine(\"hello\"); } }\n");

            store.BuildSearchIndex(snapshotId);

            var results = store.SearchSource("Console", snapshotId);
            Assert.NotEmpty(results);
            Assert.Contains(results, r => r.DocumentPath == "src/Program.cs");
        }

        [Fact]
        public void SearchSource_FindsContent_ReturnsSnippet()
        {
            var store = CreateStore();
            var snapshotId = "snap-fts-002";

            CreateSnapshotWithContent(store, snapshotId, "src/calc.cs",
                "class Calculator { int Add(int a, int b) => a + b; }");

            store.BuildSearchIndex(snapshotId);

            var results = store.SearchSource("Calculator", snapshotId);
            Assert.Single(results);
            Assert.Equal("src/calc.cs", results[0].DocumentPath);
            Assert.Contains("Calculator", results[0].Snippet);
        }

        [Fact]
        public void SearchSymbols_FindsSymbolByFqnFragment()
        {
            var store = CreateStore();
            var snapshotId = "snap-fts-003";
            CreateSnapshotWithContent(store, snapshotId, "src/Foo.cs",
                "namespace N { class Foo { } }");

            var decl = new SymbolDeclaration
            {
                SymbolId = new SymbolId("T:N.Foo", "asm1", "N.Foo"),
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "doc-src/Foo.cs:hash-src/Foo.cs",
                FullSpan = new DeclarationSpan(0, 10),
                SignatureSpan = new DeclarationSpan(0, 10),
                BodySpan = new DeclarationSpan(null, null),
                NameSpan = new DeclarationSpan(0, 3)
            };

            store.SaveDeclarations(snapshotId, [decl]);
            store.BuildSearchIndex(snapshotId);

            var results = store.SearchSymbols("Foo", snapshotId);
            Assert.NotEmpty(results);
            Assert.Contains(results, r => r.FullyQualifiedName == "N.Foo");
        }

        [Fact]
        public void SearchSymbols_FindsCourseServiceByCamelCaseSegmentFragment()
        {
            var store = CreateStore();
            var snapshotId = "snap-fts-frag-001";
            CreateSnapshotWithContent(store, snapshotId, "src/CourseService.cs",
                "namespace N.Services { class CourseService { } }");

            var decl = new SymbolDeclaration
            {
                SymbolId = new SymbolId("T:N.Services.CourseService", "asm1", "N.Services.CourseService"),
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "doc-src/CourseService.cs:hash-src/CourseService.cs",
                FullSpan = new DeclarationSpan(0, 10),
                SignatureSpan = new DeclarationSpan(0, 10),
                BodySpan = new DeclarationSpan(null, null),
                NameSpan = new DeclarationSpan(0, 3)
            };

            store.SaveDeclarations(snapshotId, [decl]);
            store.BuildSearchIndex(snapshotId);

            var results = store.SearchSymbols("Service", snapshotId);
            Assert.Contains(results, r => r.FullyQualifiedName == "N.Services.CourseService");
        }

        [Fact]
        public void SearchSymbols_FindsCourseServiceByArbitrarySubstringFragment()
        {
            var store = CreateStore();
            var snapshotId = "snap-fts-frag-002";
            CreateSnapshotWithContent(store, snapshotId, "src/CourseService.cs",
                "namespace N.Services { class CourseService { } }");

            var decl = new SymbolDeclaration
            {
                SymbolId = new SymbolId("T:N.Services.CourseService", "asm1", "N.Services.CourseService"),
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "doc-src/CourseService.cs:hash-src/CourseService.cs",
                FullSpan = new DeclarationSpan(0, 10),
                SignatureSpan = new DeclarationSpan(0, 10),
                BodySpan = new DeclarationSpan(null, null),
                NameSpan = new DeclarationSpan(0, 3)
            };

            store.SaveDeclarations(snapshotId, [decl]);
            store.BuildSearchIndex(snapshotId);

            var results = store.SearchSymbols("rseServ", snapshotId);
            Assert.Contains(results, r => r.FullyQualifiedName == "N.Services.CourseService");
        }

        [Fact]
        public void SearchSymbols_FragmentFallback_HonorsKindFilter()
        {
            var store = CreateStore();
            var snapshotId = "snap-fts-frag-003";
            CreateSnapshotWithContent(store, snapshotId, "src/CourseService.cs",
                "namespace N.Services { class CourseService { } }");

            var typeDecl = new SymbolDeclaration
            {
                SymbolId = new SymbolId("T:N.Services.CourseService", "asm1", "N.Services.CourseService"),
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "doc-src/CourseService.cs:hash-src/CourseService.cs",
                FullSpan = new DeclarationSpan(0, 10),
                SignatureSpan = new DeclarationSpan(0, 10),
                BodySpan = new DeclarationSpan(null, null),
                NameSpan = new DeclarationSpan(0, 3)
            };
            var methodDecl = new SymbolDeclaration
            {
                SymbolId = new SymbolId("M:N.Services.CourseService.GetPagedForInstructorAsync", "asm1", "N.Services.CourseService.GetPagedForInstructorAsync"),
                Kind = IndexedSymbolKind.Method,
                DocumentVersionId = "doc-src/CourseService.cs:hash-src/CourseService.cs",
                FullSpan = new DeclarationSpan(11, 20),
                SignatureSpan = new DeclarationSpan(11, 20),
                BodySpan = new DeclarationSpan(null, null),
                NameSpan = new DeclarationSpan(11, 14)
            };

            store.SaveDeclarations(snapshotId, [typeDecl, methodDecl]);
            store.BuildSearchIndex(snapshotId);

            var typeResults = store.SearchSymbols("Service", snapshotId, kind: "Type");
            Assert.Contains(typeResults, r => r.FullyQualifiedName == "N.Services.CourseService");
            Assert.DoesNotContain(typeResults, r => r.FullyQualifiedName == "N.Services.CourseService.GetPagedForInstructorAsync");

            var methodResults = store.SearchSymbols("Service", snapshotId, kind: "Method");
            Assert.Contains(methodResults, r => r.FullyQualifiedName == "N.Services.CourseService.GetPagedForInstructorAsync");
            Assert.DoesNotContain(methodResults, r => r.FullyQualifiedName == "N.Services.CourseService");
        }

        // --- PR-1: snapshot-scoped declaration joins in search / FQN resolution ---
        // Fixture mirrors retention (`keep: 3`): the same non-partial symbol "Alpha" is
        // declared in three retained snapshots, each pointing at its own document version.
        // Before the fix, declarations were joined by symbol_id alone (unscoped), so a
        // symbol with N retained declarations produced N duplicate search rows and an
        // inflated declarationCount even when only one declaration belongs to the
        // requested snapshot.

        private static SnapshotRow BuildSingleDocSnapshot(
            string snapshotId, string documentId, string relativePath, string contentHash, string content)
        {
            return new SnapshotRow
            {
                SnapshotId = snapshotId,
                WorkspaceId = "workspace:///root/proj",
                GitRoot = "/root",
                SolutionPath = "/root/proj",
                SdkVersion = "10.0.301",
                CompilerVersion = "4.12.0.0",
                CreatedAtUtc = DateTime.UtcNow,
                Documents = new List<DocumentVersion>
                {
                    new DocumentVersion(StringToBytes(content))
                    {
                        DocumentId = documentId,
                        FilePath = relativePath,
                        ContentHash = contentHash,
                        Encoding = "utf-8",
                        LineStart = "[0]",
                        CreatedAtUtc = DateTime.MinValue,
                        LineStarts = "[0]"
                    }
                }
            };
        }

        /// <summary>
        /// Creates three retained snapshots (snap1 oldest .. snap3 current) that each carry
        /// their own document version of "src/Alpha.cs" and their own declaration row for the
        /// same non-partial symbol "Retain.Alpha". Returns the three snapshot ids.
        /// </summary>
        private static (string snap1, string snap2, string snap3) CreateThreeRetainedAlphaVersions(SqliteIndexStore store)
        {
            const string documentId = "doc-alpha";
            const string relativePath = "src/Alpha.cs";
            var snap1 = "snap-retain-alpha-1";
            var snap2 = "snap-retain-alpha-2";
            var snap3 = "snap-retain-alpha-3";

            var alphaSymbol = new SymbolId("T:Retain.Alpha", "asm1", "Retain.Alpha");

            foreach (var (snapshotId, contentHash) in new[]
                     {
                         (snap1, "hash-alpha-v1"),
                         (snap2, "hash-alpha-v2"),
                         (snap3, "hash-alpha-v3"),
                     })
            {
                store.SaveSnapshot(BuildSingleDocSnapshot(snapshotId, documentId, relativePath, contentHash, "class Alpha { }"));

                store.SaveDeclarations(snapshotId, [new SymbolDeclaration
                {
                    SymbolId = alphaSymbol,
                    Kind = IndexedSymbolKind.Type,
                    DocumentVersionId = $"{documentId}:{contentHash}",
                    FullSpan = new DeclarationSpan(0, 10),
                    SignatureSpan = new DeclarationSpan(0, 10),
                    BodySpan = new DeclarationSpan(null, null),
                    NameSpan = new DeclarationSpan(0, 5)
                }]);
                store.BuildSearchIndex(snapshotId);
            }

            return (snap1, snap2, snap3);
        }

        /// <summary>
        /// Adds a partial type "Retain.Beta" with two current declarations (two document
        /// versions, both bound to the given snapshot) plus a non-partial "Retain.Gamma"
        /// declared once, all in one additional snapshot save so the fixture snapshot keeps
        /// a single SnapshotRow per id.
        /// </summary>
        private void AddBetaAndGammaToSnapshot(SqliteIndexStore store, string snapshotId)
        {
            // Bind three more document versions into the *existing* snapshot. Calling
            // store.SaveSnapshot(...) again for the same snapshotId would violate the
            // snapshots table primary key, so the documents/document_versions/
            // snapshot_documents rows are inserted directly, mirroring what
            // InsertDocumentsAndBindings does for a fresh snapshot.
            using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
            {
                connection.Open();
                foreach (var (documentId, relativePath, contentHash, content) in new[]
                         {
                             ("doc-beta-part1", "src/BetaPart1.cs", "hash-beta-p1", "partial class Beta { void A() {} }"),
                             ("doc-beta-part2", "src/BetaPart2.cs", "hash-beta-p2", "partial class Beta { void B() {} }"),
                             ("doc-gamma", "src/Gamma.cs", "hash-gamma", "class Gamma { }"),
                         })
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO documents (document_id, relative_path, last_changed_snapshot_id)
                        VALUES (@documentId, @relativePath, @snapshotId)
                        ON CONFLICT(document_id) DO UPDATE SET last_changed_snapshot_id = excluded.last_changed_snapshot_id;

                        INSERT OR IGNORE INTO document_versions (document_version_id, document_id, content_hash, content, encoding, line_starts)
                        VALUES (@documentVersionId, @documentId, @contentHash, @content, 'utf-8', '[0]');

                        INSERT INTO snapshot_documents (snapshot_id, document_version_id)
                        VALUES (@snapshotId, @documentVersionId);
                    ";
                    cmd.Parameters.AddWithValue("@documentId", documentId);
                    cmd.Parameters.AddWithValue("@relativePath", relativePath);
                    cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
                    cmd.Parameters.AddWithValue("@documentVersionId", $"{documentId}:{contentHash}");
                    cmd.Parameters.AddWithValue("@contentHash", contentHash);
                    cmd.Parameters.AddWithValue("@content", StringToBytes(content));
                    cmd.ExecuteNonQuery();
                }
            }

            var betaSymbol = new SymbolId("T:Retain.Beta", "asm1", "Retain.Beta");
            var gammaSymbol = new SymbolId("T:Retain.Gamma", "asm1", "Retain.Gamma");

            store.SaveDeclarations(snapshotId,
            [
                new SymbolDeclaration
                {
                    SymbolId = betaSymbol,
                    Kind = IndexedSymbolKind.Type,
                    DocumentVersionId = "doc-beta-part1:hash-beta-p1",
                    FullSpan = new DeclarationSpan(0, 10),
                    SignatureSpan = new DeclarationSpan(0, 10),
                    BodySpan = new DeclarationSpan(null, null),
                    NameSpan = new DeclarationSpan(0, 5),
                    IsPartial = true
                },
                new SymbolDeclaration
                {
                    SymbolId = betaSymbol,
                    Kind = IndexedSymbolKind.Type,
                    DocumentVersionId = "doc-beta-part2:hash-beta-p2",
                    FullSpan = new DeclarationSpan(0, 10),
                    SignatureSpan = new DeclarationSpan(0, 10),
                    BodySpan = new DeclarationSpan(null, null),
                    NameSpan = new DeclarationSpan(0, 5),
                    IsPartial = true
                },
                new SymbolDeclaration
                {
                    SymbolId = gammaSymbol,
                    Kind = IndexedSymbolKind.Type,
                    DocumentVersionId = "doc-gamma:hash-gamma",
                    FullSpan = new DeclarationSpan(0, 10),
                    SignatureSpan = new DeclarationSpan(0, 10),
                    BodySpan = new DeclarationSpan(null, null),
                    NameSpan = new DeclarationSpan(0, 5)
                },
            ]);
            store.BuildSearchIndex(snapshotId);
        }

        [Fact]
        public void SearchSymbols_WithRetainedVersions_ReturnsEachSymbolOnce()
        {
            var store = CreateStore();
            var (_, _, snap3) = CreateThreeRetainedAlphaVersions(store);

            var results = store.SearchSymbols("Alpha", snap3);

            Assert.Single(results);
            Assert.Equal("Retain.Alpha", results[0].FullyQualifiedName);
        }

        [Fact]
        public void SearchSymbolsBySubstring_WithRetainedVersions_ReturnsEachSymbolOnce()
        {
            var store = CreateStore();
            var (_, _, snap3) = CreateThreeRetainedAlphaVersions(store);

            // "lph" matches no whole FTS token, forcing the substring fallback path.
            var results = store.SearchSymbols("lph", snap3);

            Assert.Single(results);
            Assert.Equal("Retain.Alpha", results[0].FullyQualifiedName);
        }

        [Fact]
        public void ResolveSymbolByFqn_NonPartialType_ReportsOneDeclarationInRequestedSnapshot()
        {
            var store = CreateStore();
            var (_, _, snap3) = CreateThreeRetainedAlphaVersions(store);

            var info = store.ResolveSymbolByFqn("Retain.Alpha", snap3);

            Assert.NotNull(info);
            Assert.Equal(1, info!.DeclarationCount);
            Assert.False(info.IsPartial);
        }

        [Fact]
        public void ResolveSymbolByFqn_PartialType_ReportsAllDeclarationsInRequestedSnapshot()
        {
            var store = CreateStore();
            var (_, _, snap3) = CreateThreeRetainedAlphaVersions(store);
            AddBetaAndGammaToSnapshot(store, snap3);

            var info = store.ResolveSymbolByFqn("Retain.Beta", snap3);

            Assert.NotNull(info);
            Assert.Equal(2, info!.DeclarationCount);
            Assert.True(info.IsPartial);
        }

        [Fact]
        public void SearchSymbols_LimitCountsDistinctSymbols_NotDeclarations()
        {
            var store = CreateStore();
            var (_, _, snap3) = CreateThreeRetainedAlphaVersions(store);
            AddBetaAndGammaToSnapshot(store, snap3);

            // Current snapshot has three distinct symbols (Alpha, Beta, Gamma); Alpha alone
            // carries three retained declaration rows. Before the fix, Alpha's fan-out could
            // consume the whole --limit window on its own.
            var results = store.SearchSymbols("Retain", snap3, limit: 2);

            Assert.Equal(2, results.Count);
            Assert.Equal(2, results.Select(r => r.FullyQualifiedName).Distinct().Count());
        }

        [Fact]
        public void SearchSymbolsPage_WithLimitOne_PagesThroughAllDistinctSymbolsWithoutDuplicatesOrGaps()
        {
            var store = CreateStore();
            var (_, _, snap3) = CreateThreeRetainedAlphaVersions(store);
            AddBetaAndGammaToSnapshot(store, snap3);

            var seen = new List<string>();
            SearchCursor? cursor = null;
            for (var i = 0; i < 10; i++)
            {
                var page = store.SearchSymbolsPage("Retain", snap3, limit: 1, includeGenerated: false, kind: null, cursor);
                Assert.Single(page.Items);
                seen.Add(page.Items[0].FullyQualifiedName);

                if (page.NextCursor == null)
                    break;

                cursor = SearchCursor.TryDecode(page.NextCursor);
                Assert.NotNull(cursor);
            }

            Assert.Equal(3, seen.Count);
            Assert.Equal(3, seen.Distinct().Count());
            Assert.Equal(
                store.SearchSymbols("Retain", snap3).Select(r => r.FullyQualifiedName).OrderBy(x => x),
                seen.OrderBy(x => x));
        }

        [Fact]
        public void SearchSymbolsPage_LastPage_ReturnsNullNextCursor()
        {
            var store = CreateStore();
            var (_, _, snap3) = CreateThreeRetainedAlphaVersions(store);
            AddBetaAndGammaToSnapshot(store, snap3);

            var page = store.SearchSymbolsPage("Retain", snap3, limit: 10, includeGenerated: false, kind: null, cursor: null);

            Assert.Equal(3, page.Items.Count);
            Assert.Null(page.NextCursor);
        }

        [Fact]
        public void SearchSymbolsPage_CursorFromDifferentQuery_ThrowsRatherThanReturningWrongRows()
        {
            var store = CreateStore();
            var (_, _, snap3) = CreateThreeRetainedAlphaVersions(store);
            AddBetaAndGammaToSnapshot(store, snap3);

            var page = store.SearchSymbolsPage("Retain", snap3, limit: 1, includeGenerated: false, kind: null, cursor: null);
            var cursor = SearchCursor.TryDecode(page.NextCursor!);

            Assert.Throws<ArgumentException>(() =>
                store.SearchSymbolsPage("SomethingElse", snap3, limit: 1, includeGenerated: false, kind: null, cursor));
        }

        [Fact]
        public void SearchCursor_TryDecode_GarbageInput_ReturnsNull()
        {
            Assert.Null(SearchCursor.TryDecode("not-a-valid-cursor!!"));
        }

        // Mode selects which keyset decoder reads the cursor's sort key, so an
        // unrecognised mode must be rejected rather than falling through to the
        // substring decoder and reinterpreting a rank-keyed cursor as FQN-keyed.
        [Fact]
        public void SearchCursor_Validate_RejectsForeignSnapshotQueryAndUnknownMode()
        {
            var fingerprint = SearchCursor.ComputeFingerprint("Retain", null, includeGenerated: false);
            var cursor = new SearchCursor("snap-a", fingerprint, "fts", -1.5, "Ns.Retain", "T:Ns.Retain|prod");

            cursor.Validate("snap-a", fingerprint);

            Assert.Throws<ArgumentException>(() => cursor.Validate("snap-b", fingerprint));
            Assert.Throws<ArgumentException>(() => cursor.Validate("snap-a", "a-different-request"));
            Assert.Throws<ArgumentException>(() =>
                (cursor with { Mode = "not-a-mode" }).Validate("snap-a", fingerprint));
        }

        [Fact]
        public void SearchSymbols_SymbolWithNoDeclarations_RemainsSearchable()
        {
            var store = CreateStore();
            var snapshotId = "snap-external-only";
            CreateSnapshotWithContent(store, snapshotId, "src/External.cs", "class External { }");

            InsertMetadataOnlySymbol(snapshotId, "T:Retain.ExternalOnly", "asm1", "Retain.ExternalOnly", "Type");
            store.BuildSearchIndex(snapshotId);

            var results = store.SearchSymbols("ExternalOnly", snapshotId);
            Assert.Contains(results, r => r.FullyQualifiedName == "Retain.ExternalOnly");
        }

        private void InsertMetadataOnlySymbol(string snapshotId, string docCommentId, string assembly, string fqn, string kind)
        {
            var symbolId = new SymbolId(docCommentId, assembly, fqn).Value;

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO symbols (symbol_id, doc_comment_id, assembly_identity, kind, fqn)
                VALUES (@symbolId, @docCommentId, @assembly, @kind, @fqn);
                INSERT INTO snapshot_symbols (snapshot_id, symbol_id, fqn)
                VALUES (@snapshotId, @symbolId, @fqn);
            ";
            cmd.Parameters.AddWithValue("@symbolId", symbolId);
            cmd.Parameters.AddWithValue("@docCommentId", docCommentId);
            cmd.Parameters.AddWithValue("@assembly", assembly);
            cmd.Parameters.AddWithValue("@kind", kind);
            cmd.Parameters.AddWithValue("@fqn", fqn);
            cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
            cmd.ExecuteNonQuery();
        }

        [Fact]
        public void SearchSymbols_ExcludesGeneratedDeclarations_WhenScopedToSnapshot()
        {
            var store = CreateStore();
            var snapshotId = "snap-generated-scoped";
            CreateSnapshotWithContent(store, snapshotId, "src/GeneratedOnly.cs", "class GeneratedOnly { }");

            store.SaveDeclarations(snapshotId,
            [
                new SymbolDeclaration
                {
                    SymbolId = new SymbolId("T:Retain.GeneratedOnly", "asm1", "Retain.GeneratedOnly"),
                    Kind = IndexedSymbolKind.Type,
                    DocumentVersionId = "doc-src/GeneratedOnly.cs:hash-src/GeneratedOnly.cs",
                    FullSpan = new DeclarationSpan(0, 10),
                    SignatureSpan = new DeclarationSpan(0, 10),
                    BodySpan = new DeclarationSpan(null, null),
                    NameSpan = new DeclarationSpan(0, 5),
                    IsGenerated = true,
                    GeneratorIdentity = "test-generator"
                }
            ]);
            store.BuildSearchIndex(snapshotId);

            Assert.Empty(store.SearchSymbols("GeneratedOnly", snapshotId));
            var withGenerated = store.SearchSymbols("GeneratedOnly", snapshotId, includeGenerated: true);
            Assert.Contains(withGenerated, r => r.FullyQualifiedName == "Retain.GeneratedOnly");
        }

        [Fact]
        public void SearchSymbols_OlderSnapshotId_ReturnsThatSnapshotsView()
        {
            var store = CreateStore();
            var (snap1, snap2, snap3) = CreateThreeRetainedAlphaVersions(store);

            var infoSnap1 = store.ResolveSymbolByFqn("Retain.Alpha", snap1);
            var infoSnap2 = store.ResolveSymbolByFqn("Retain.Alpha", snap2);
            var infoSnap3 = store.ResolveSymbolByFqn("Retain.Alpha", snap3);

            Assert.NotNull(infoSnap1);
            Assert.NotNull(infoSnap2);
            Assert.NotNull(infoSnap3);
            Assert.Equal(1, infoSnap1!.DeclarationCount);
            Assert.Equal(1, infoSnap2!.DeclarationCount);
            Assert.Equal(1, infoSnap3!.DeclarationCount);

            var resultsSnap1 = store.SearchSymbols("Alpha", snap1);
            Assert.Single(resultsSnap1);
        }

        [Fact]
        public void SearchSymbols_QueryPlan_DeclarationAndSnapshotDocumentIndexesExist()
        {
            // PR-1's scoped EXISTS predicates rely on these two indexes for an index seek
            // rather than a table scan (idx_declarations_symbol_id from Migration 003,
            // ux_snapshot_documents from Migration 019). On the handful of rows this fixture
            // creates, SQLite's cost-based planner can legitimately prefer a full scan over
            // either index, so this asserts the indexes are present rather than asserting
            // EXPLAIN QUERY PLAN chose one : planner choice on tiny tables is not a
            // meaningful regression signal, index availability is.
            var store = CreateStore();
            CreateThreeRetainedAlphaVersions(store);

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            Assert.True(IndexExists(connection, "declarations", "idx_declarations_symbol_id"));
            Assert.True(IndexExists(connection, "snapshot_documents", "ux_snapshot_documents"));
        }

        private static bool IndexExists(SqliteConnection connection, string table, string indexName)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA index_list(" + table + ");";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1) == indexName)
                    return true;
            }
            return false;
        }

        [Fact]
        public void SearchSource_EmptyQuery_ReturnsEmptyList()
        {
            var store = CreateStore();
            var snapshotId = "snap-fts-004";
            CreateSnapshotWithContent(store, snapshotId, "src/a.cs", "class A { }");
            store.BuildSearchIndex(snapshotId);

            var results = store.SearchSource("NonExistentTermXYZ", snapshotId);
            Assert.Empty(results);
        }

        [Fact]
        public void SearchSymbols_EmptyQuery_ReturnsEmptyList()
        {
            var store = CreateStore();
            var snapshotId = "snap-fts-005";
            CreateSnapshotWithContent(store, snapshotId, "src/a.cs", "class A { }");

            var decl = new SymbolDeclaration
            {
                SymbolId = new SymbolId("T:A", "asm1", "A"),
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "doc-src/a.cs:hash-src/a.cs",
                FullSpan = new DeclarationSpan(0, 10),
                SignatureSpan = new DeclarationSpan(0, 10),
                BodySpan = new DeclarationSpan(null, null),
                NameSpan = new DeclarationSpan(0, 1)
            };

            store.SaveDeclarations(snapshotId, [decl]);
            store.BuildSearchIndex(snapshotId);

            var results = store.SearchSymbols("NonExistentSymbol", snapshotId);
            Assert.Empty(results);
        }

        [Fact]
        public void ResolveSymbolByFqn_ExactMatch_ReturnsSymbol()
        {
            var store = CreateStore();
            var snapshotId = "snap-fts-006";
            CreateSnapshotWithContent(store, snapshotId, "src/a.cs", "class A { }");

            var decl = new SymbolDeclaration
            {
                SymbolId = new SymbolId("T:A", "asm1", "MyNs.A"),
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "doc-src/a.cs:hash-src/a.cs",
                FullSpan = new DeclarationSpan(0, 10),
                SignatureSpan = new DeclarationSpan(0, 10),
                BodySpan = new DeclarationSpan(null, null),
                NameSpan = new DeclarationSpan(0, 1)
            };

            store.SaveDeclarations(snapshotId, [decl]);

            var info = store.ResolveSymbolByFqn("MyNs.A", snapshotId);
            Assert.NotNull(info);
            Assert.Equal("T:A", info!.SymbolId.DocCommentId);
            Assert.Equal("MyNs.A", info.FullyQualifiedName);
        }

        [Fact]
        public void ResolveSymbolByFqn_PartialPrefixMatch_ReturnsSymbol()
        {
            var store = CreateStore();
            var snapshotId = "snap-fts-007";
            CreateSnapshotWithContent(store, snapshotId, "src/a.cs", "class A { }");

            var decl = new SymbolDeclaration
            {
                SymbolId = new SymbolId("T:A", "asm1", "MyNs.MyClass"),
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "doc-src/a.cs:hash-src/a.cs",
                FullSpan = new DeclarationSpan(0, 10),
                SignatureSpan = new DeclarationSpan(0, 10),
                BodySpan = new DeclarationSpan(null, null),
                NameSpan = new DeclarationSpan(0, 1)
            };

            store.SaveDeclarations(snapshotId, [decl]);

            var info = store.ResolveSymbolByFqn("MyNs.My", snapshotId);
            Assert.NotNull(info);
            Assert.Equal("MyNs.MyClass", info!.FullyQualifiedName);
        }

        [Fact]
        public void ResolveSymbolByFqn_NoMatch_ReturnsNull()
        {
            var store = CreateStore();
            var snapshotId = "snap-fts-008";
            CreateSnapshotWithContent(store, snapshotId, "src/a.cs", "class A { }");

            var info = store.ResolveSymbolByFqn("Does.Not.Exist", snapshotId);
            Assert.Null(info);
        }

        [Fact]
        public void SearchSource_SnapshotIsolation_ReturnsOnlyScopedResults()
        {
            var store = CreateStore();
            var snap1 = "snap-fts-iso-1";
            var snap2 = "snap-fts-iso-2";

            CreateSnapshotWithContent(store, snap1, "src/a.cs", "class Alpha { }");
            store.BuildSearchIndex(snap1);

            CreateSnapshotWithContent(store, snap2, "src/b.cs", "class Beta { }");
            store.BuildSearchIndex(snap2);

            var results1 = store.SearchSource("Alpha", snap1);
            Assert.NotEmpty(results1);

            var results2 = store.SearchSource("Alpha", snap2);
            Assert.Empty(results2);
        }

        [Fact]
        public void SearchSource_SnapshotIsolation_UsesVersionBoundToRequestedSnapshot()
        {
            var store = CreateStore();
            var snap1 = "snap-fts-iso-version-1";
            var snap2 = "snap-fts-iso-version-2";

            CreateSnapshotWithContent(store, snap1, "src/shared.cs", "class Shared { string OldMarker = \"old\"; }");
            store.SaveDeclarations(snap1,
            [
                new SymbolDeclaration
                {
                    SymbolId = new SymbolId("T:GeneratedShared", "asm1", "GeneratedShared"),
                    Kind = IndexedSymbolKind.Type,
                    DocumentVersionId = "doc-src/shared.cs:hash-src/shared.cs",
                    FullSpan = new DeclarationSpan(0, 10),
                    SignatureSpan = new DeclarationSpan(0, 10),
                    BodySpan = new DeclarationSpan(null, null),
                    NameSpan = new DeclarationSpan(0, 10),
                    IsGenerated = true,
                    GeneratorIdentity = "test-generator"
                }
            ]);
            store.BuildSearchIndex(snap1);

            var manifest = new SnapshotRow
            {
                SnapshotId = snap2,
                WorkspaceId = "workspace:///root/proj",
                GitRoot = "/root",
                SolutionPath = "/root/proj",
                SdkVersion = "10.0.301",
                CompilerVersion = "4.12.0.0",
                CreatedAtUtc = DateTime.UtcNow,
                Documents =
                [
                    new DocumentVersion(StringToBytes("class Shared { string NewMarker = \"new\"; }"))
                    {
                        DocumentId = "doc-shared-v2",
                        FilePath = "src/shared.cs",
                        ContentHash = "hash-shared-v2",
                        Encoding = "utf-8",
                        LineStart = "[0]",
                        CreatedAtUtc = DateTime.MinValue,
                        LineStarts = "[0]"
                    }
                ]
            };
            store.SaveSnapshot(manifest);
            store.BuildSearchIndex(snap2);

            Assert.Empty(store.SearchSource("old", snap2));
            var current = store.SearchSource("new", snap2);
            Assert.Single(current);
            Assert.Equal("src/shared.cs", current[0].DocumentPath);
        }

        [Fact]
        public void SearchSource_EmptyQueryAndNonPositiveLimit_ReturnEmpty()
        {
            var store = CreateStore();
            var snapshotId = "snap-fts-bounds-001";
            CreateSnapshotWithContent(store, snapshotId, "src/a.cs", "class A { }");
            store.BuildSearchIndex(snapshotId);

            Assert.Empty(store.SearchSource("", snapshotId));
            Assert.Empty(store.SearchSource("class", snapshotId, limit: 0));
        }

        [Fact]
        public void Migration005_CreatesOperationalTables()
        {
            var runner = new MigrationRunner(_dbPath);
            runner.RunMigrations();
            Assert.Equal(VersionConstants.DatabaseSchemaVersion, runner.GetCurrentSchemaVersion());

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();

            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='edges';";
            Assert.NotNull(cmd.ExecuteScalar());

            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='diagnostics';";
            Assert.NotNull(cmd.ExecuteScalar());

            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='annotations';";
            Assert.NotNull(cmd.ExecuteScalar());
        }

        [Fact]
        public void Migration004_CreatesFtsTables()
        {
            var runner = new MigrationRunner(_dbPath);
            runner.RunMigrations();
            Assert.Equal(VersionConstants.DatabaseSchemaVersion, runner.GetCurrentSchemaVersion());

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();

            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='source_fts';";
            var sourceTable = cmd.ExecuteScalar();
            Assert.NotNull(sourceTable);

            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='symbol_fts';";
            var symbolTable = cmd.ExecuteScalar();
            Assert.NotNull(symbolTable);
        }
    }
}
