// Purpose: focused tests for the B1 member-edge extractors (Calls/Reads/Writes/Overrides/Hides/Returns/Throws/Constructs).
// Owns: the B1MemberEdgeExtractorTests class and its Roslyn compilation helpers.
// Must not contain: unrelated extractor/adapter tests, or storage/migration tests.

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
    public class B1MemberEdgeExtractorTests
    {
        private static Compilation CreateCompilation(string source, string path = "test.cs")
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source, path: path);
            return CSharpCompilation.Create(
                "TestAssembly",
                [syntaxTree],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        }

        private static IReadOnlyDictionary<DocumentId, DocumentVersionId> CreateDocVersions(string path)
        {
            return new Dictionary<DocumentId, DocumentVersionId>
            {
                { new DocumentId(path), DocumentVersionId.Compute("test-content") }
            };
        }

        [Fact]
        public void Declares_ClassWithOneMethod_EmitsDeclaresEdge()
        {
            var source = @"
class Foo {
    void Bar() {}
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-decl", "/");

            var edges = extractor.ExtractAll();

            var declares = edges.Where(e => e.Kind == "Declares").ToList();
            Assert.NotEmpty(declares);
            Assert.Contains(declares, e =>
                e.SourceSymbolId.Contains("Foo") &&
                e.TargetSymbolId.Contains("Bar"));
        }

        [Fact]
        public void Calls_MethodACallsMethodB_EmitsCallsEdge()
        {
            var source = @"
class Foo {
    void A() { B(); }
    void B() {}
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-calls", "/");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Calls" &&
                e.SourceSymbolId.Contains('A') &&
                e.TargetSymbolId.Contains('B'));
        }

        [Fact]
        public void Calls_StaticQualifiedMethod_EmitsReferencesEdgeToContainingType()
        {
            var source = @"
static class Helper {
    public static void DoWork() { }
}
class Caller {
    void Execute() { Helper.DoWork(); }
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-ref-type", "/");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "References" &&
                e.SourceSymbolId.Contains("Execute") &&
                e.TargetSymbolId.Contains("Helper"));
            Assert.Contains(edges, e =>
                e.Kind == "Calls" &&
                e.SourceSymbolId.Contains("Execute") &&
                e.TargetSymbolId.Contains("DoWork"));
        }

        [Fact]
        public void Calls_InterfaceInvocation_PersistsStaticReceiverType()
        {
            var source = @"
interface IBase { void Run(); }
interface INarrow : IBase { }
class Caller { void Execute(INarrow receiver) { receiver.Run(); } }";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-receiver", "/");

            var call = Assert.Single(extractor.ExtractAll(), e =>
                e.Kind == "Calls" && e.SourceSymbolId.Contains("Execute") && e.TargetSymbolId.Contains("Run"));
            var alternatives = ReceiverTypeConstraints.Deserialize(call.ReceiverTypeConstraintsJson);

            var requiredType = Assert.Single(Assert.Single(alternatives));
            Assert.Contains("INarrow", requiredType, StringComparison.Ordinal);
        }

        [Fact]
        public void Calls_GenericReceiver_PersistsAllNamedTypeConstraints()
        {
            var source = @"
interface IRun { void Run(); }
interface IMarker { }
class Caller { void Execute<T>(T receiver) where T : IRun, IMarker { receiver.Run(); } }";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-generic-receiver", "/");

            var call = Assert.Single(extractor.ExtractAll(), e =>
                e.Kind == "Calls" && e.SourceSymbolId.Contains("Execute") && e.TargetSymbolId.Contains("Run"));
            var requiredTypes = Assert.Single(ReceiverTypeConstraints.Deserialize(call.ReceiverTypeConstraintsJson));

            Assert.Equal(2, requiredTypes.Count);
            Assert.Contains(requiredTypes, id => id.Contains("IRun", StringComparison.Ordinal));
            Assert.Contains(requiredTypes, id => id.Contains("IMarker", StringComparison.Ordinal));
        }

        [Fact]
        public void Calls_GenericReceiverWithUnpersistedSpecialConstraint_EmitsNoCandidateEvidence()
        {
            var source = @"
interface IRun { void Run(); }
class Caller { void Execute<T>(T receiver) where T : class, IRun { receiver.Run(); } }";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-special-constraint", "/");

            var call = Assert.Single(extractor.ExtractAll(), e =>
                e.Kind == "Calls" && e.SourceSymbolId.Contains("Execute") && e.TargetSymbolId.Contains("Run"));

            Assert.Null(call.ReceiverTypeConstraintsJson);
        }

        [Fact]
        public void Calls_SameRelationThroughDifferentReceivers_PreservesAlternativeReceiverSets()
        {
            var source = @"
interface IBase { void Run(); }
interface ILeft : IBase { }
interface IRight : IBase { }
class Caller { void Execute(ILeft left, IRight right) { left.Run(); right.Run(); } }";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-receiver-alternatives", "/");

            var call = Assert.Single(extractor.ExtractAll(), e =>
                e.Kind == "Calls" && e.SourceSymbolId.Contains("Execute") && e.TargetSymbolId.Contains("Run"));
            var alternatives = ReceiverTypeConstraints.Deserialize(call.ReceiverTypeConstraintsJson);

            Assert.Equal(2, alternatives.Count);
            Assert.Contains(alternatives, constraint => constraint.Single().Contains("ILeft", StringComparison.Ordinal));
            Assert.Contains(alternatives, constraint => constraint.Single().Contains("IRight", StringComparison.Ordinal));
        }

        [Fact]
        public void BindingIncompleteness_AmbiguousOverload_IsReasonCoded()
        {
            var source = @"
class Foo {
    void Pick(int x, double y) {}
    void Pick(double x, int y) {}
    void A() { Pick(1, 1); }
}";
            var compilation = CreateCompilation(source);
            var collector = new BindingIncompletenessCollector("TestProject", "/");
            var extractor = new MemberEdgeExtractor(
                compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(),
                "snap-incomplete-ambiguous", "/", null, collector);

            extractor.ExtractAll();

            Assert.Contains(collector.ToRecords(), record =>
                record.Reason == BindingIncompletenessReason.AmbiguousOverload && record.Count > 0);
        }

        [Fact]
        public void BindingIncompleteness_MissingType_IsReasonCodedAndPersisted()
        {
            var source = @"
class Foo {
    object A() { return new Missing.ExternalType(); }
}";
            var compilation = CreateCompilation(source);
            var collector = new BindingIncompletenessCollector("TestProject", "/");
            var extractor = new MemberEdgeExtractor(
                compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(),
                "snap-incomplete-metadata", "/", null, collector);

            extractor.ExtractAll();
            var records = collector.ToRecords();

            Assert.Contains(records, record =>
                record.Reason == BindingIncompletenessReason.UnresolvedMetadata && record.Count > 0);

            var dbPath = Path.Combine(Path.GetTempPath(), $"lurp-binding-{Guid.NewGuid():N}.db");
            using var store = new SqliteIndexStore(dbPath);
            try
            {
                store.Open();
                store.RunMigrations();
                store.SaveBindingIncompleteness("snap-incomplete-metadata", records);

                var persisted = store.GetBindingIncompleteness("snap-incomplete-metadata", "TestProject");
                Assert.Contains(persisted, record =>
                    record.Reason == BindingIncompletenessReason.UnresolvedMetadata && record.Count > 0);
            }
            finally
            {
                store.Close();
                if (File.Exists(dbPath)) File.Delete(dbPath);
            }
        }

        // filtered_external was declared as a reason code but never recorded at
        // any call site : the constant existed while the report and TRUST_KERNEL
        // listed it as a live reason. A resolved binding whose target lives in an
        // assembly outside the compilation is filtered from the persisted graph
        // (external symbols are never declared in the snapshot), so the reason
        // must be measured at the extraction site.
        [Fact]
        public void BindingIncompleteness_FilteredExternalTarget_IsReasonCoded()
        {
            var source = @"
class Foo {
    void A() { GetHashCode(); }
}";
            var compilation = CreateCompilation(source);
            var collector = new BindingIncompletenessCollector("TestProject", "/");
            var extractor = new MemberEdgeExtractor(
                compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(),
                "snap-filtered-external", "/", null, collector);

            extractor.ExtractAll();

            Assert.Contains(collector.ToRecords(), record =>
                record.Reason == BindingIncompletenessReason.FilteredExternal && record.Count > 0);
        }

        [Fact]
        public void Calls_OverloadedBinaryOperator_EmitsCallsEdge()
        {
            var source = @"
class Money
{
    public static Money operator +(Money left, Money right) => left;

    public Money Add(Money other) => this + other;
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-operator", "/");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Calls" &&
                e.SourceSymbolId.Contains("Add") &&
                e.TargetSymbolId.Contains("op_Addition"));
        }

        [Fact]
        public void Calls_UserDefinedConversion_EmitsCallsEdge()
        {
            var source = @"
class Fraction
{
    public static explicit operator int(Fraction value) => 1;

    public int ToInt() => (int)this;
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-conversion", "/");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Calls" &&
                e.SourceSymbolId.Contains("ToInt") &&
                e.TargetSymbolId.Contains("op_Explicit"));
        }

        [Fact]
        public void Calls_TwoCallSitesForSameRelation_PersistsOneEdge()
        {
            var source = @"
class Foo {
    void A() { B(); B(); }
    void B() {}
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(
                compilation,
                CreateDocVersions("test.cs"),
                new HashSet<DocumentId>(),
                "snap-call-sites",
                "/");

            var edges = extractor.ExtractAll()
                .Where(e => e.Kind == "Calls" && e.SourceSymbolId.Contains("A") && e.TargetSymbolId.Contains("B"))
                .ToList();

            Assert.Single(edges);

            var dbPath = Path.Combine(Path.GetTempPath(), $"indexer_call_sites_{Guid.NewGuid():N}.db");
            try
            {
                using var store = new SqliteIndexStore(dbPath);
                store.Open();
                store.RunMigrations();
                store.SaveEdges("snap-call-sites", edges);

                var persisted = store.GetEdges("snap-call-sites")
                    .Where(e => e.Kind == "Calls" && e.SourceSymbolId.Contains("A") && e.TargetSymbolId.Contains("B"))
                    .ToList();

                Assert.Single(persisted);
                Assert.Equal(edges[0].ReceiverTypeConstraintsJson, persisted[0].ReceiverTypeConstraintsJson);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath))
                    File.Delete(dbPath);
            }
        }

        [Fact]
        public void Calls_ExtensionMethod_EmitsExtensionReceiverEdge()
        {
            var source = @"
static class Extensions
{
    public static void Bar(this Foo foo) {}
}
class Foo
{
    void A()
    {
        this.Bar();
    }
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-ext", "/");

            var edges = extractor.ExtractAll();

            // The Calls edge is still emitted (caller -> extension method)
            Assert.Contains(edges, e =>
                e.Kind == "Calls" &&
                e.SourceSymbolId.Contains("A") &&
                e.TargetSymbolId.Contains("Bar"));

            // The ExtensionReceiver edge goes from receiver type to extension method
            Assert.Contains(edges, e =>
                e.Kind == "ExtensionReceiver" &&
                e.SourceSymbolId.Contains("Foo") &&
                e.TargetSymbolId.Contains("Bar"));
        }

        [Fact]
        public void Calls_ExtensionMethod_StaticCall_NoExtensionReceiverEdge()
        {
            var source = @"
static class Extensions
{
    public static void Bar(this Foo foo) {}
}
class Foo
{
    void A()
    {
        Extensions.Bar(this); // static call, not extension syntax
    }
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-ext-static", "/");

            var edges = extractor.ExtractAll();

            // The Calls edge is emitted
            Assert.Contains(edges, e =>
                e.Kind == "Calls" &&
                e.SourceSymbolId.Contains("A") &&
                e.TargetSymbolId.Contains("Bar"));

            // No ExtensionReceiver edge for static-call syntax
            Assert.DoesNotContain(edges, e => e.Kind == "ExtensionReceiver");
        }

        [Fact]
        public void Calls_IndexerGetter_EmitsReadsEdge()
        {
            var source = @"
class Wrapper
{
    public int this[string key] => key.Length;

    public int GetItem(string key) => this[key];
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-indexer-get", "/");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Reads" &&
                e.SourceSymbolId.Contains("GetItem") &&
                e.TargetSymbolId.Contains(".Item("));
        }

        [Fact]
        public void Calls_IndexerSetter_EmitsWritesEdge()
        {
            var source = @"
class Box
{
    private int _value;
    public int this[int i] { get => _value; set => _value = value; }

    public void Store(int i, int v) { this[i] = v; }
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-indexer-set", "/");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Writes" &&
                e.SourceSymbolId.Contains("Store") &&
                e.TargetSymbolId.Contains(".Item("));
        }

        [Fact]
        public void Calls_IndexerMultipleAccessSites_DeduplicatesByRelation()
        {
            var source = @"
class Bag
{
    private int _value;
    public int this[int i] { get => _value; set => _value = value; }

    public void Use()
    {
        var x = this[0];
        var y = this[1];
        this[2] = 3;
        this[3] = 4;
    }
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-indexer-dedup", "/");

            var edges = extractor.ExtractAll()
                .Where(e => e.SourceSymbolId.Contains("Use") && e.TargetSymbolId.Contains(".Item("))
                .ToList();

            Assert.Equal(2, edges.Count);
            Assert.Contains(edges, e => e.Kind == "Reads");
            Assert.Contains(edges, e => e.Kind == "Writes");
        }

        [Fact]
        public void Constructs_MethodNewFoo_EmitsConstructsEdge()
        {
            var source = @"
class Foo {
    public Foo() {}
}
class Bar {
    void M() { var x = new Foo(); }
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-ctor", "/");

            var edges = extractor.ExtractAll();

            // Gap #3 (59c98c3): Constructs targets the *containing type*, not the
            // constructor member, so implicit ctors can't produce ghost member ids.
            // The `T:` prefix is the contract; asserting "#ctor" here is what made
            // this test stale.
            Assert.Contains(edges, e =>
                e.Kind == "Constructs" &&
                e.SourceSymbolId.Contains('M') &&
                e.TargetSymbolId.StartsWith("T:") &&
                e.TargetSymbolId.Contains("Foo"));
        }

        [Fact]
        public void Overrides_DerivedOverridesVirtual_EmitsOverridesEdge()
        {
            var source = @"
class Base {
    public virtual void M() {}
}
class Derived : Base {
    public override void M() {}
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-override", "/");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Overrides" &&
                e.SourceSymbolId.Contains("Derived") &&
                e.TargetSymbolId.Contains("Base"));
        }

        [Fact]
        public void Hides_DerivedHidesBaseMethod_EmitsHidesEdge()
        {
            var source = @"
class Base {
    public void M() {}
}
class Derived : Base {
    public new void M() {}
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-hides", "/");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Hides" &&
                e.SourceSymbolId.Contains("Derived") &&
                e.TargetSymbolId.Contains("Base"));
        }

        [Fact]
        public void Hides_DerivedHidesBaseProperty_EmitsHidesEdge()
        {
            var source = @"
class Base {
    public int P { get; set; }
}
class Derived : Base {
    public new int P { get; set; }
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-hides-prop", "/");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Hides" &&
                e.SourceSymbolId.Contains("Derived") &&
                e.TargetSymbolId.Contains("Base"));
        }

        [Fact]
        public void Hides_OverloadWithDifferentParams_DoesNotEmitHidesEdge()
        {
            // Different parameter count/types = overloading, not hiding
            var source = @"
class Base {
    public void M(int x) {}
}
class Derived : Base {
    public void M(string x) {}
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-hides-overload", "/");

            var edges = extractor.ExtractAll();

            Assert.DoesNotContain(edges, e => e.Kind == "Hides");
        }

        [Fact]
        public void Hides_OverrideDoesNotAlsoEmitHidesEdge()
        {
            // An override should emit Overrides, not also Hides
            var source = @"
class Base {
    public virtual void M() {}
}
class Derived : Base {
    public override void M() {}
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-hides-no-double", "/");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e => e.Kind == "Overrides");
            Assert.DoesNotContain(edges, e =>
                e.Kind == "Hides" &&
                e.SourceSymbolId.Contains("Derived.M") &&
                e.TargetSymbolId.Contains("Base.M"));
        }

        [Fact]
        public void ReadsWrites_MethodReadsAndWritesField_EmitsBothEdges()
        {
            var source = @"
class Foo {
    int _field;
    void M() { _field = 1; int x = _field; }
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-rw", "/");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Writes" &&
                e.SourceSymbolId.Contains('M') &&
                e.TargetSymbolId.Contains("_field"));

            Assert.Contains(edges, e =>
                e.Kind == "Reads" &&
                e.SourceSymbolId.Contains('M') &&
                e.TargetSymbolId.Contains("_field"));
        }

        [Fact]
        public void Returns_MethodWithNonVoidReturn_EmitsReturnsEdge()
        {
            var source = @"
class Foo {
    string M() { return ""; }
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-ret", "/");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Returns" &&
                e.SourceSymbolId.Contains('M') &&
                e.TargetSymbolId.Contains("String"));
        }

        [Fact]
        public void Throws_MethodThrowsException_EmitsThrowsEdge()
        {
            var source = @"
class Foo {
    void M() { throw new System.InvalidOperationException(); }
}";
            var compilation = CreateCompilation(source);
            var extractor = new MemberEdgeExtractor(compilation, CreateDocVersions("test.cs"), new HashSet<DocumentId>(), "snap-throw", "/");

            var edges = extractor.ExtractAll();

            Assert.Contains(edges, e =>
                e.Kind == "Throws" &&
                e.SourceSymbolId.Contains('M') &&
                e.TargetSymbolId.Contains("InvalidOperationException"));
        }
    }
}
