using System.Collections.Immutable;
using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

/// <summary>
///     The DI registrations of a project. Each registration call is identified by the declaring type
///     of the bound method (S02-F18), its types are read from the bound type and value arguments (D26),
///     and its edges are owned by the D8 owner of the call (S02-F8).
/// </summary>
public sealed class DependencyInjectionAdapter : IFrameworkAdapter
{
    internal const string ServiceCollectionType = "Microsoft.Extensions.DependencyInjection.IServiceCollection";
    private const string ServiceDescriptorType = "Microsoft.Extensions.DependencyInjection.ServiceDescriptor";
    private const string ServiceExtensionsType = "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions";
    private const string DescriptorExtensionsType = "Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions";
    private const string HostedServiceExtensionsType = "Microsoft.Extensions.DependencyInjection.ServiceCollectionHostedServiceExtensions";
    private const string OptionsExtensionsType = "Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions";
    private const string OptionsConfigurationExtensionsType = "Microsoft.Extensions.DependencyInjection.OptionsConfigurationServiceCollectionExtensions";
    private const string RelationalOptionsBuilderType = "Microsoft.EntityFrameworkCore.Infrastructure.RelationalDbContextOptionsBuilder`2";
    private const string ScrutorExtensionsType = "Microsoft.Extensions.DependencyInjection.ServiceCollectionExtensions";

    // The assemblies of MS.DI itself. A method of theirs that registers nothing (BuildServiceProvider,
    // RemoveAll) is not an unknown registration.
    private static readonly HashSet<string> ContainerAssemblies = new(StringComparer.Ordinal)
    {
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.DependencyInjection.Abstractions"
    };

    public string Name => "Dependency Injection";
    public string Version => "di-v3";
    public string Description => "Dependency injection registrations (MS.DI, options, hosted services, Scrutor scans)";

    public AdapterExtractionResult Extract(AdapterExtractionContext context)
    {
        var edges = new List<EdgeRecord>();
        var annotations = new List<AnnotationRecord>();

        // No IServiceCollection type: no call of the project can bind to a registration API.
        if (context.Compilation.GetTypesByMetadataName(ServiceCollectionType).IsEmpty)
            return edges;

        foreach (var (owner, node) in context.Bodies())
        {
            var ownerId = context.MakeSymbolId(owner);
            if (ownerId == null)
                continue;

            var call = new RegistrationCall(context, Version, edges, ownerId, annotations);
            var model = context.GetSemanticModel(node.SyntaxTree);
            foreach (var root in ExecutableRoots.Operations(node, model))
            {
                foreach (var invocation in root.DescendantsAndSelf().OfType<IInvocationOperation>())
                    ReadInvocation(invocation, call);
            }
        }

        return new AdapterExtractionResult(edges, annotations);
    }

    private static void ReadInvocation(IInvocationOperation invocation, RegistrationCall call)
    {
        var method = invocation.TargetMethod;
        var declaringType = FrameworkTypes.DeclaringType(method);
        var location = invocation.Syntax.GetLocation();

        if (FrameworkTypes.Is(declaringType, ServiceExtensionsType))
        {
            ReadRegistration(method, invocation.Arguments, location, call);
        }
        else if (FrameworkTypes.Is(declaringType, DescriptorExtensionsType))
        {
            ReadDescriptorExtension(method, invocation.Arguments, location, call);
        }
        else if (IsServiceDescriptorCollectionAdd(method))
        {
            ReadDescriptor(Argument(invocation.Arguments, "item"), location, call);
        }
        else if (FrameworkTypes.Is(declaringType, HostedServiceExtensionsType))
        {
            // di_hosted_service: the hosted type is known, the host lifecycle is not modeled.
            call.Unknown(call.OwnerId, location);
            if (Argument(invocation.Arguments, "implementationFactory") == null)
                call.OwnerClaim(method.TypeArguments, location);
        }
        else if (FrameworkTypes.Is(declaringType, OptionsExtensionsType) || FrameworkTypes.Is(declaringType, OptionsConfigurationExtensionsType))
        {
            // di_options: the options type is known, the configuration binding is not modeled.
            call.Unknown(call.OwnerId, location);
            call.OwnerClaim(method.TypeArguments, location);
            if (method.Name is "Configure" or "AddOptions" && method.TypeArguments is [var options])
                call.OptionsBinding(options, location);
        }
        else if (FrameworkTypes.Is(declaringType, ScrutorExtensionsType, "Scrutor") && method.Name == "Scan")
        {
            DependencyInjectionConventionMatcher.ReadScan(invocation, location, call);
        }
        else if (IsExternalServiceCollectionMethod(method, call.Context.Compilation))
        {
            call.Unknown(call.OwnerId, location);
            var declared = method.ReducedFrom ?? method;
            if (declared.IsExtensionMethod && FrameworkTypes.Is(declared.Parameters[0].Type, ServiceCollectionType) &&
                !call.Context.IsSolutionSymbol(declared) && !IsMediatRMethod(declared) &&
                !ReadExternalScan(invocation, location, call) && method.TypeArguments.Length == 1)
            {
                call.OwnerClaim(method.TypeArguments, location);
            }
        }
    }

    private static bool ReadExternalScan(IInvocationOperation invocation, Location location, RegistrationCall call)
    {
        var scans = invocation.Arguments
            .SelectMany(a => a.Value.DescendantsAndSelf().OfType<IAnonymousFunctionOperation>())
            .SelectMany(lambda => lambda.Body.Descendants().OfType<IInvocationOperation>())
            .Where(candidate => !call.Context.IsSolutionSymbol(candidate.TargetMethod) &&
                                !IsMediatRMethod(candidate.TargetMethod) && !IsEfProviderOption(candidate.TargetMethod) &&
                                HasAssemblyParameter(candidate.TargetMethod))
            .Distinct()
            .ToList();
        if (HasAssemblyParameter(invocation.TargetMethod))
            scans.Add(invocation);

        var assemblies = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
        foreach (var scan in scans)
        {
            foreach (var type in scan.TargetMethod.TypeArguments)
            {
                if (KnownType(type)?.ContainingAssembly is { } assembly)
                    assemblies.Add(assembly);
            }

            foreach (var argument in scan.Arguments.Where(a =>
                         a.Parameter != null && IsScanSelector(scan.TargetMethod, a.Parameter)))
            {
                foreach (var value in argument.Value.DescendantsAndSelf())
                {
                    var assembly = value switch
                    {
                        ITypeOfOperation typeOf => KnownType(typeOf.TypeOperand)?.ContainingAssembly,
                        IInvocationOperation assemblyCall when assemblyCall.TargetMethod.Name == "GetExecutingAssembly" &&
                            FrameworkTypes.Is(assemblyCall.TargetMethod.ContainingType, "System.Reflection.Assembly")
                            => call.Context.Compilation.Assembly,
                        _ => null
                    };

                    if (assembly != null)
                        assemblies.Add(assembly);
                }
            }
        }

        foreach (var assembly in assemblies)
            call.Add(call.OwnerId, GraphNodeIds.AssemblyScanConventionPrefix + assembly.Identity.GetDisplayName(),
                Provenance.Convention, location, targetIsBoundary: true);

        return scans.Count > 0;
    }

    private static bool IsMediatRMethod(IMethodSymbol method)
    {
        return method.ContainingAssembly?.Name is "MediatR" or "MediatR.Extensions.Microsoft.DependencyInjection";
    }

    /// <summary>
    ///     An option of an EF Core relational provider. <c>MigrationsAssembly</c> names an assembly,
    ///     but it registers no service.
    /// </summary>
    private static bool IsEfProviderOption(IMethodSymbol method)
    {
        return FrameworkTypes.Is(method.ContainingType, RelationalOptionsBuilderType);
    }

    private static bool HasAssemblyParameter(IMethodSymbol method)
    {
        return NamesAssembly(method.Name) || method.Parameters.Any(IsAssemblySelector);
    }

    private static bool IsAssemblySelector(IParameterSymbol parameter)
    {
        var type = parameter.Type;
        return NamesAssembly(parameter.Name) || IsAssembly(type) ||
               type is IArrayTypeSymbol array && IsAssembly(array.ElementType) ||
               type is INamedTypeSymbol { TypeArguments: [var element] } named &&
                   FrameworkTypes.Is(named, "System.Collections.Generic.IEnumerable`1") && IsAssembly(element);
    }

    /// <summary>
    ///     A scan method named after assemblies can also take a <see cref="System.Type" /> whose
    ///     assembly is scanned (<c>AddValidatorsFromAssemblyContaining(typeof(T))</c>).
    /// </summary>
    private static bool IsTypeSelector(IParameterSymbol parameter)
    {
        var type = parameter.Type;
        return IsType(type) ||
               type is IArrayTypeSymbol array && IsType(array.ElementType) ||
               type is INamedTypeSymbol { TypeArguments: [var element] } named &&
                   FrameworkTypes.Is(named, "System.Collections.Generic.IEnumerable`1") && IsType(element);
    }

    private static bool IsType(ITypeSymbol type)
    {
        return FrameworkTypes.Is(type, "System.Type");
    }

    /// <summary>One argument of a scan call that names the scanned assembly.</summary>
    private static bool IsScanSelector(IMethodSymbol method, IParameterSymbol parameter)
    {
        // The receiver of an extension method is the service collection, never a scan target.
        if (method.IsExtensionMethod && parameter.Ordinal == 0 &&
            FrameworkTypes.Is(parameter.Type, ServiceCollectionType))
            return false;

        return IsAssemblySelector(parameter) || (NamesAssembly(method.Name) && IsTypeSelector(parameter));
    }

    private static bool NamesAssembly(string name)
    {
        return name.Contains("assembly", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("assemblies", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAssembly(ITypeSymbol type)
    {
        return FrameworkTypes.Is(type, "System.Reflection.Assembly");
    }

    /// <summary>
    ///     <c>ServiceCollectionDescriptorExtensions</c>: a <c>descriptor</c> parameter is read as a
    ///     descriptor, a <c>descriptors</c> sequence is not read, every other <c>TryAdd*</c> method is
    ///     a registration, and <c>RemoveAll*</c> registers nothing.
    /// </summary>
    private static void ReadDescriptorExtension(IMethodSymbol method, ImmutableArray<IArgumentOperation> arguments, Location location, RegistrationCall call)
    {
        if (Argument(arguments, "descriptor") is { } descriptor)
            ReadDescriptor(descriptor, location, call);
        else if (Argument(arguments, "descriptors") != null)
            call.Unknown(call.OwnerId, location);
        else if (method.Name.StartsWith("TryAdd", StringComparison.Ordinal))
            ReadRegistration(method, arguments, location, call);
    }

    /// <summary>
    ///     A <see cref="ServiceDescriptorType" /> value: a call of one of its static factory methods or
    ///     one of its constructors is read like a registration; any other value is not readable.
    /// </summary>
    private static void ReadDescriptor(IOperation? value, Location location, RegistrationCall call)
    {
        switch (WithoutImplicitConversions(value))
        {
            case IInvocationOperation { TargetMethod.IsStatic: true } factory when FrameworkTypes.Is(factory.TargetMethod.ContainingType, ServiceDescriptorType):
                ReadRegistration(factory.TargetMethod, factory.Arguments, location, call);
                break;
            case IObjectCreationOperation { Constructor: { } constructor } creation when FrameworkTypes.Is(constructor.ContainingType, ServiceDescriptorType):
                ReadRegistration(constructor, creation.Arguments, location, call);
                break;
            default:
                call.Unknown(call.OwnerId, location);
                break;
        }
    }

    /// <summary>
    ///     D26: the service and the implementation of one registration, from the type arguments
    ///     (<c>TService</c>, <c>TImplementation</c>) and the value arguments by parameter name. A
    ///     registration without an implementation argument registers the service type itself.
    /// </summary>
    private static void ReadRegistration(IMethodSymbol method, ImmutableArray<IArgumentOperation> arguments, Location location, RegistrationCall call)
    {
        ITypeSymbol? service = null;
        ITypeSymbol? implementation = null;
        var serviceUnknown = false;
        var implementationUnknown = false;

        for (var i = 0; i < method.TypeParameters.Length; i++)
        {
            switch (method.TypeParameters[i].Name)
            {
                case "TService":
                    service = KnownType(method.TypeArguments[i]);
                    serviceUnknown = service == null;
                    break;
                case "TImplementation":
                    implementation = KnownType(method.TypeArguments[i]);
                    implementationUnknown = implementation == null;
                    break;
            }
        }

        foreach (var argument in arguments)
        {
            switch (argument.Parameter?.Name)
            {
                case "serviceType" or "service":
                    service = TypeOfOperand(argument.Value);
                    serviceUnknown = service == null;
                    break;
                case "implementationType":
                    implementation = TypeOfOperand(argument.Value);
                    implementationUnknown = implementation == null;
                    break;
                case "implementationFactory" or "factory":
                    implementationUnknown = true;
                    break;
                case "implementationInstance" or "instance":
                    implementation = InstanceType(argument.Value);
                    implementationUnknown = implementation == null;
                    break;
            }
        }

        var serviceId = service == null ? null : call.Context.MakeSymbolId(service);
        if (serviceUnknown || serviceId == null)
        {
            call.Unknown(call.OwnerId, location);
            return;
        }

        if (implementationUnknown)
        {
            call.Unknown(serviceId, location);
            return;
        }

        var implementationId = call.Context.MakeSymbolId(implementation ?? service!);
        if (implementationId == null)
        {
            call.Unknown(serviceId, location);
            return;
        }

        call.Add(serviceId, implementationId, Provenance.FrameworkDerived, location);
        call.Add(call.OwnerId, implementationId, Provenance.FrameworkDerived, location);
    }

    /// <summary><c>ICollection&lt;ServiceDescriptor&gt;.Add</c> and <c>IList&lt;ServiceDescriptor&gt;.Insert</c> on a service collection.</summary>
    private static bool IsServiceDescriptorCollectionAdd(IMethodSymbol method)
    {
        return method.ContainingType is { TypeArguments: [var element] } collection &&
               FrameworkTypes.Is(element, ServiceDescriptorType) &&
               ((method.Name == "Add" && FrameworkTypes.Is(collection, "System.Collections.Generic.ICollection`1")) ||
                (method.Name == "Insert" && FrameworkTypes.Is(collection, "System.Collections.Generic.IList`1")));
    }

    /// <summary>
    ///     D26: a method outside the compilation that takes an <c>IServiceCollection</c> (an extension
    ///     method included) and is not a method of the MS.DI assemblies.
    /// </summary>
    private static bool IsExternalServiceCollectionMethod(IMethodSymbol method, Compilation compilation)
    {
        var declared = (method.ReducedFrom ?? method).OriginalDefinition;
        if (!ExtractionUtils.IsOutsideCompilation(declared, compilation))
            return false;
        if (declared.ContainingAssembly is { } assembly && ContainerAssemblies.Contains(assembly.Name))
            return false;
        return declared.Parameters.Any(parameter => FrameworkTypes.Is(parameter.Type, ServiceCollectionType));
    }

    private static IOperation? Argument(ImmutableArray<IArgumentOperation> arguments, string parameterName)
    {
        return arguments.FirstOrDefault(argument => argument.Parameter?.Name == parameterName)?.Value;
    }

    private static IOperation? WithoutImplicitConversions(IOperation? value)
    {
        while (value is IConversionOperation { IsImplicit: true } conversion)
            value = conversion.Operand;
        return value;
    }

    private static ITypeSymbol? KnownType(ITypeSymbol? type)
    {
        return type is null or { TypeKind: TypeKind.Error or TypeKind.TypeParameter } ? null : type;
    }

    /// <summary>The type of a <c>typeof(T)</c> argument; null for any other <c>Type</c> value (D26).</summary>
    private static ITypeSymbol? TypeOfOperand(IOperation value)
    {
        return WithoutImplicitConversions(value) is ITypeOfOperation typeOf ? KnownType(typeOf.TypeOperand) : null;
    }

    /// <summary>
    ///     D146: the static type of an instance argument when it names the run-time type: a class that
    ///     is not abstract, or a struct. An interface, an abstract class, a type parameter or a null
    ///     value does not name it.
    /// </summary>
    private static ITypeSymbol? InstanceType(IOperation value)
    {
        return WithoutImplicitConversions(value)?.Type is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct, IsAbstract: false } type
            ? type
            : null;
    }

    /// <summary>The edge sink of one D8 owner.</summary>
    internal sealed class RegistrationCall(
        AdapterExtractionContext context, string version, List<EdgeRecord> edges, string ownerId, List<AnnotationRecord> annotations)
    {
        internal AdapterExtractionContext Context => context;
        internal string OwnerId => ownerId;

        internal void Add(string sourceId, string targetId, string provenance, Location location, bool targetIsBoundary = false)
        {
            edges.Add(context.MakeEdge(sourceId, targetId, nameof(EdgeKind.Registers), provenance, version,
                context.LocationResolver.Resolve(location), targetIsBoundary: targetIsBoundary));
        }

        internal void OptionsBinding(ITypeSymbol options, Location location)
        {
            if (KnownType(options) is { } known && context.MakeSymbolId(known) is { } id)
                annotations.Add(new AnnotationRecord(id, "options_binding", "configured", context.LocationResolver.Resolve(location).path));
        }

        /// <summary>A registration that Lurp cannot read: source → the runtime-unknown node.</summary>
        internal void Unknown(string sourceId, Location location)
        {
            Add(sourceId, GraphNodeIds.RuntimeUnknown, Provenance.RuntimeUnknown, location, targetIsBoundary: true);
        }

        /// <summary>The owner → each named type of an unmodeled registration form, <c>runtime_unknown</c>.</summary>
        internal void OwnerClaim(ImmutableArray<ITypeSymbol> types, Location location)
        {
            foreach (var type in types)
            {
                if (KnownType(type) is { } known && context.MakeSymbolId(known) is { } typeId)
                    Add(ownerId, typeId, Provenance.RuntimeUnknown, location);
            }
        }
    }
}
