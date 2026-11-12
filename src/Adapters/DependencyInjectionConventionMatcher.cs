// Purpose: read a Scrutor Scan call (D28).
// Owns: the modeled chain, the Scrutor selection rules and the convention:assembly_scan placeholder.
// Must not contain: other registration forms (DependencyInjectionAdapter).

using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Lurp.Adapters;

internal static class DependencyInjectionConventionMatcher
{
    private const string ScrutorAssembly = "Scrutor";
    private const string AssemblySelectorType = "Scrutor.IAssemblySelector";
    private const string ImplementationTypeSelectorType = "Scrutor.IImplementationTypeSelector";
    private const string ServiceTypeSelectorType = "Scrutor.IServiceTypeSelector";
    private const string LifetimeSelectorType = "Scrutor.ILifetimeSelector";

    /// <summary>
    ///     D28: the chain <c>FromAssemblyOf&lt;T&gt;()</c> or <c>FromAssembliesOf(typeof(T), ...)</c>,
    ///     then <c>AddClasses()</c> or <c>AddClasses(bool)</c>, then <c>AsImplementedInterfaces()</c>,
    ///     then at most one lifetime call, gives the interface → class pairs that Scrutor registers.
    ///     Every other scan gives the <c>convention:assembly_scan:&lt;assembly&gt;</c> placeholder for
    ///     each assembly it names statically, and the runtime-unknown node when it scans an assembly
    ///     that is not known statically.
    /// </summary>
    internal static void ReadScan(IInvocationOperation scan, Location location, DependencyInjectionAdapter.RegistrationCall call)
    {
        var action = Argument(scan, "action");
        if (ModeledChain(action) is { } chain)
        {
            foreach (var assembly in chain.Assemblies)
                AddScannedPairs(assembly, chain.PublicOnly, location, call);

            // The pairs of a scan of this project change when a type of any document changes, and no
            // persisted edge leads the incremental refresh to the scan site (convention_scan).
            if (chain.Assemblies.Contains(call.Context.Compilation.Assembly, SymbolEqualityComparer.Default))
                call.Context.Incompleteness?.RecordConventionScan(scan.Syntax);
            return;
        }

        var unknown = false;
        var assemblies = new List<IAssemblySymbol>();
        foreach (var from in FromCalls(action))
        {
            if (StaticAssemblies(from) is { } known)
                assemblies.AddRange(known);
            else
                unknown = true;
        }

        foreach (var assembly in assemblies.Distinct<IAssemblySymbol>(SymbolEqualityComparer.Default))
            call.Add(call.OwnerId, $"{GraphNodeIds.AssemblyScanConventionPrefix}{assembly.Identity.GetDisplayName()}", Provenance.Convention,
                location, GraphNodeKind.Convention);

        if (unknown || assemblies.Count == 0)
            call.Unknown(call.OwnerId, location);
    }

    /// <summary>The assemblies and the <c>publicOnly</c> value of a modeled chain; null for any other scan.</summary>
    private static (List<IAssemblySymbol> Assemblies, bool PublicOnly)? ModeledChain(IOperation? action)
    {
        if (action is not IDelegateCreationOperation { Target: IAnonymousFunctionOperation lambda } ||
            lambda.Symbol.Parameters is not [var selector])
            return null;

        var statements = lambda.Body.Operations
            .Where(operation => operation is not IReturnOperation { IsImplicit: true, ReturnedValue: null })
            .ToList();
        if (statements is not [IExpressionStatementOperation { Operation: IInvocationOperation outer }])
            return null;

        var call = outer;
        if (IsScrutor(call, LifetimeSelectorType) &&
            call.TargetMethod.Name is "WithScopedLifetime" or "WithSingletonLifetime" or "WithTransientLifetime" or "WithLifetime")
            call = Receiver(call);

        if (call == null || !IsScrutor(call, ServiceTypeSelectorType) || call.TargetMethod.Name != "AsImplementedInterfaces" || call.Arguments.Length != 0)
            return null;

        call = Receiver(call);
        if (call == null || !IsScrutor(call, ImplementationTypeSelectorType) || call.TargetMethod.Name != "AddClasses")
            return null;

        bool publicOnly;
        switch (call.Arguments)
        {
            case []:
                publicOnly = true;
                break;
            case [{ Parameter.Name: "publicOnly", Value.ConstantValue: { HasValue: true, Value: bool value } }]:
                publicOnly = value;
                break;
            default:
                return null;
        }

        call = Receiver(call);
        if (call == null || !IsScrutor(call, AssemblySelectorType) || StaticAssemblies(call) is not { } assemblies)
            return null;

        if (WithoutImplicitConversions(call.Instance) is not IParameterReferenceOperation reference ||
            !SymbolEqualityComparer.Default.Equals(reference.Parameter, selector))
            return null;

        return (assemblies, publicOnly);
    }

    /// <summary>
    ///     Scrutor 4.2.2 (P2-S07 oracle run): <c>AddClasses</c> selects the classes and delegates that
    ///     are not abstract and not static, with <c>publicOnly</c> only the ones whose own declared
    ///     accessibility is public; <c>AsImplementedInterfaces</c> registers every interface of the
    ///     class, and for a generic class only an interface with the same number of type arguments
    ///     (the containing types counted).
    /// </summary>
    private static void AddScannedPairs(IAssemblySymbol assembly, bool publicOnly, Location location, DependencyInjectionAdapter.RegistrationCall call)
    {
        foreach (var type in ExtractionUtils.GetAllNamedTypes(assembly.GlobalNamespace))
        {
            if (type.TypeKind is not (TypeKind.Class or TypeKind.Delegate) || type.IsAbstract || type.IsStatic)
                continue;
            if (publicOnly && type.DeclaredAccessibility != Accessibility.Public)
                continue;

            var typeId = call.Context.MakeSymbolId(type);
            if (typeId == null)
                continue;

            var arity = Arity(type);
            foreach (var iface in type.AllInterfaces)
            {
                if (iface.TypeKind == TypeKind.Error || (arity > 0 && Arity(iface) != arity))
                    continue;

                if (call.Context.MakeSymbolId(iface) is { } ifaceId)
                    call.Add(ifaceId, typeId, Provenance.Convention, location);
            }
        }
    }

    /// <summary>The <c>From*</c> calls of Scrutor's assembly selector inside the scan action.</summary>
    private static IEnumerable<IInvocationOperation> FromCalls(IOperation? action)
    {
        return action == null
            ? []
            : action.DescendantsAndSelf().OfType<IInvocationOperation>()
                .Where(invocation => IsScrutor(invocation, AssemblySelectorType) && invocation.TargetMethod.Name.StartsWith("From", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The assemblies of <c>FromAssemblyOf&lt;T&gt;()</c> and of <c>FromAssembliesOf(params Type[])</c>
    ///     when every type is a <c>typeof</c>; null for any other selection.
    /// </summary>
    private static List<IAssemblySymbol>? StaticAssemblies(IInvocationOperation from)
    {
        switch (from.TargetMethod.Name)
        {
            case "FromAssemblyOf" when from.TargetMethod.TypeArguments is [{ TypeKind: not TypeKind.Error, ContainingAssembly: { } assembly }]:
                return [assembly];
            case "FromAssembliesOf" when Argument(from, "types") is IArrayCreationOperation { Initializer: { } initializer }:
                var assemblies = new List<IAssemblySymbol>();
                foreach (var element in initializer.ElementValues)
                {
                    if (WithoutImplicitConversions(element) is not ITypeOfOperation { TypeOperand: { TypeKind: not TypeKind.Error, ContainingAssembly: { } elementAssembly } })
                        return null;
                    if (!assemblies.Contains(elementAssembly, SymbolEqualityComparer.Default))
                        assemblies.Add(elementAssembly);
                }

                return assemblies.Count == 0 ? null : assemblies;
            default:
                return null;
        }
    }

    private static bool IsScrutor(IInvocationOperation invocation, string typeName)
    {
        return FrameworkTypes.Is(FrameworkTypes.DeclaringType(invocation.TargetMethod), typeName, ScrutorAssembly);
    }

    private static IInvocationOperation? Receiver(IInvocationOperation invocation)
    {
        return WithoutImplicitConversions(invocation.Instance) as IInvocationOperation;
    }

    private static IOperation? Argument(IInvocationOperation invocation, string parameterName)
    {
        return invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == parameterName)?.Value;
    }

    private static IOperation? WithoutImplicitConversions(IOperation? value)
    {
        while (value is IConversionOperation { IsImplicit: true } conversion)
            value = conversion.Operand;
        return value;
    }

    /// <summary>The number of type arguments of <paramref name="type" /> and of its containing types (the run-time arity).</summary>
    private static int Arity(INamedTypeSymbol type)
    {
        var arity = 0;
        for (var current = type; current != null; current = current.ContainingType)
            arity += current.Arity;
        return arity;
    }
}
