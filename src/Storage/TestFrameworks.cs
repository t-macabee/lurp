namespace Lurp.Shared;

internal static class TestFrameworks
{
    internal const string FrameworkAssembliesText =
        "Microsoft.VisualStudio.TestPlatform.ObjectModel, Microsoft.Testing.Platform, xunit.core, xunit.v3.core, nunit.framework, Microsoft.VisualStudio.TestPlatform.TestFramework, MSTest.TestFramework, TUnit.Core";

    internal static readonly string[] FrameworkAssemblySimpleNames =
        FrameworkAssembliesText.Split(", ");

    private static readonly HashSet<string> FrameworkAssemblySet =
        new(FrameworkAssemblySimpleNames, StringComparer.Ordinal);

    internal static readonly string[] TestAttributeMetadataNames =
    [
        "Xunit.FactAttribute",
        "NUnit.Framework.TestAttribute",
        "NUnit.Framework.TestCaseAttribute",
        "NUnit.Framework.TestCaseSourceAttribute",
        "NUnit.Framework.TheoryAttribute",
        "Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute",
        "TUnit.Core.TestAttribute"
    ];

    internal static readonly string[] SetupAttributeMetadataNames =
    [
        "NUnit.Framework.SetUpAttribute",
        "NUnit.Framework.OneTimeSetUpAttribute",
        "Microsoft.VisualStudio.TestTools.UnitTesting.TestInitializeAttribute",
        "Microsoft.VisualStudio.TestTools.UnitTesting.ClassInitializeAttribute",
        "TUnit.Core.BeforeAttribute"
    ];

    internal static bool IsTestProjectReferenceSet(IEnumerable<string> referenceSimpleNames)
    {
        foreach (var name in referenceSimpleNames)
        {
            if (FrameworkAssemblySet.Contains(name))
                return true;
        }

        return false;
    }
}
