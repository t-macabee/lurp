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
    public class B2PolymorphismExtractorTests
    {
        private static Compilation CreateCompilation(string source, string path = "test.cs")
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source, path: path);
            return CSharpCompilation.Create(
                "TestAssembly",
                [syntaxTree],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        }

        [Fact]
        public void InterfaceDispatch_ClassImplementsInterface_EmitsMayDispatchTo()
        {
            var source = @"
interface IFoo {
    void Bar();
}
class Foo : IFoo {
    public void Bar() {}
}";
            var compilation = CreateCompilation(source);
            var extractor = new PolymorphismExtractor(compilation, "snap-poly-iface", "/");

            var edges = extractor.ExtractAll();

            var dispatchEdge = Assert.Single(edges, e => e.Kind == "MayDispatchTo");
            Assert.Equal("compiler_proved", dispatchEdge.Provenance);
            Assert.Contains("IFoo", dispatchEdge.SourceSymbolId);
            Assert.Contains("Foo", dispatchEdge.TargetSymbolId);
            Assert.Contains("Bar", dispatchEdge.TargetSymbolId);
        }

        [Fact]
        public void VirtualOverride_DerivedOverridesVirtual_EmitsMayDispatchTo()
        {
            var source = @"
class Base {
    public virtual void M() {}
}
class Derived : Base {
    public override void M() {}
}";
            var compilation = CreateCompilation(source);
            var extractor = new PolymorphismExtractor(compilation, "snap-poly-virt", "/");

            var edges = extractor.ExtractAll();

            var dispatchEdge = Assert.Single(edges, e => e.Kind == "MayDispatchTo");
            Assert.Equal("compiler_proved", dispatchEdge.Provenance);
            Assert.Contains("Base", dispatchEdge.SourceSymbolId);
            Assert.Contains("Derived", dispatchEdge.TargetSymbolId);
            Assert.Contains("M", dispatchEdge.TargetSymbolId);
        }

        [Fact]
        public void InterfaceDispatch_GenericInterface_CapturesTypeArguments()
        {
            var source = @"
using System.Collections.Generic;
interface IRepository<T> { void Save(T item); }
class Customer { }
class CustomerRepo : IRepository<Customer> { public void Save(Customer item) {} }
";
            var compilation = CreateCompilation(source);
            var extractor = new PolymorphismExtractor(compilation, "snap-poly-generic", "/");

            var edges = extractor.ExtractAll();

            var dispatchEdge = Assert.Single(edges, e => e.Kind == "MayDispatchTo");
            Assert.NotNull(dispatchEdge.TypeArgumentsJson);
            Assert.Contains("Customer", dispatchEdge.TypeArgumentsJson);
        }

        [Fact]
        public void InterfaceDispatch_GenericInterface_DifferentTypeArgsProduceDistinctEdges()
        {
            var source = @"
interface IRepository<T> { void Save(T item); }
class Customer { }
class Order { }
class CustomerRepo : IRepository<Customer> { public void Save(Customer item) {} }
class OrderRepo : IRepository<Order> { public void Save(Order item) {} }
";
            var compilation = CreateCompilation(source);
            var extractor = new PolymorphismExtractor(compilation, "snap-poly-generic-multi", "/");

            var edges = extractor.ExtractAll();

            var dispatchEdges = edges.Where(e => e.Kind == "MayDispatchTo").ToList();
            Assert.Equal(2, dispatchEdges.Count);

            var customerEdge = Assert.Single(dispatchEdges, e => e.TargetSymbolId.Contains("CustomerRepo"));
            Assert.NotNull(customerEdge.TypeArgumentsJson);
            Assert.Contains("Customer", customerEdge.TypeArgumentsJson);

            var orderEdge = Assert.Single(dispatchEdges, e => e.TargetSymbolId.Contains("OrderRepo"));
            Assert.NotNull(orderEdge.TypeArgumentsJson);
            Assert.Contains("Order", orderEdge.TypeArgumentsJson);
        }

        [Fact]
        public void InterfaceDispatch_NonGenericInterface_TypeArgumentsJsonIsNull()
        {
            var source = @"
interface IFoo { void Bar(); }
class Foo : IFoo { public void Bar() {} }
";
            var compilation = CreateCompilation(source);
            var extractor = new PolymorphismExtractor(compilation, "snap-poly-nongeneric", "/");

            var edges = extractor.ExtractAll();

            var dispatchEdge = Assert.Single(edges, e => e.Kind == "MayDispatchTo");
            Assert.Null(dispatchEdge.TypeArgumentsJson);
        }

        [Fact]
        public void InterfaceDispatch_MultiTypeParamGeneric_CapturesAllTypeArguments()
        {
            var source = @"
using System.Collections.Generic;
interface IMapper<TSource, TDest> { TDest Map(TSource input); }
class Source { }
class Dest { }
class MyMapper : IMapper<Source, Dest> { public Dest Map(Source input) => new Dest(); }
";
            var compilation = CreateCompilation(source);
            var extractor = new PolymorphismExtractor(compilation, "snap-poly-multi-generic", "/");

            var edges = extractor.ExtractAll();

            var dispatchEdge = Assert.Single(edges, e => e.Kind == "MayDispatchTo");
            Assert.NotNull(dispatchEdge.TypeArgumentsJson);
            Assert.Contains("Source", dispatchEdge.TypeArgumentsJson);
            Assert.Contains("Dest", dispatchEdge.TypeArgumentsJson);
        }
    }
}
