using Lurp.Storage;
using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

/// <summary>How much of a project's compilation Roslyn actually managed to bind.</summary>
internal enum CompilationReadability
{
    /// <summary>Reference resolution succeeded. Binding results are trustworthy.</summary>
    Readable,

    /// <summary>
    /// Core reference assemblies are missing. Nothing this compilation reports about
    /// symbol relationships can be trusted, including the absence of a relationship.
    /// </summary>
    Blind,
}

/// <summary>
/// Classifies compilations before extraction runs, so an unreadable project is
/// reported as unreadable instead of being indexed into a confident empty graph.
/// </summary>
/// <remarks>
/// MSBuildWorkspace does not throw when project evaluation fails. It returns a
/// project whose metadata references were never resolved, and Roslyn then reports
/// CS0518 ("predefined type 'System.Object' is not defined") across every document.
/// Extraction over such a compilation still succeeds mechanically — it yields
/// declarations with almost no edges — and a capsule built from it reports
/// "directCallers: []" as a proved absence rather than as an inability to look.
/// This gate exists to make that state unrepresentable.
/// </remarks>
internal static class WorkspaceLoadGate
{
    internal static CompilationReadability Classify(Compilation compilation)
    {
        // System.Object binding to an error type means the compilation has no corlib:
        // MSBuild never handed Roslyn a reference set. That is always an environment
        // fault (missing restore, absent SDK, uninstalled target framework) and never
        // a property of the source under analysis.
        var objectType = compilation.GetSpecialType(SpecialType.System_Object);
        return objectType.TypeKind == TypeKind.Error
            ? CompilationReadability.Blind
            : CompilationReadability.Readable;
    }

    /// <summary>
    /// Records every document of an unreadable project as unresolved, so capsules
    /// anchored in it can distinguish "no relationships exist" from "no relationships
    /// were observable". One record per document keeps the blind set document-precise
    /// and reuses the existing incompleteness channel end to end.
    /// </summary>
    internal static List<BindingIncompletenessRecord> DescribeBlindProject(
        Compilation compilation, string projectName, string gitRoot)
    {
        var records = new List<BindingIncompletenessRecord>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            if (string.IsNullOrEmpty(tree.FilePath))
                continue;

            records.Add(new BindingIncompletenessRecord(
                projectName,
                DocumentChangeDetector.GetRelativePath(tree.FilePath, gitRoot),
                BindingIncompletenessReason.ProjectUnreadable,
                Count: 1,
                VersionConstants.ExtractorVersion));
        }

        // A project with no syntax trees still needs one project-scoped marker.
        if (records.Count == 0)
        {
            records.Add(new BindingIncompletenessRecord(
                projectName,
                DocumentPath: null,
                BindingIncompletenessReason.ProjectUnreadable,
                Count: 1,
                VersionConstants.ExtractorVersion));
        }

        return records;
    }

    /// <summary>
    /// Operator-facing remediation for a blind project. The failure is in the build
    /// environment, so the message names what to run rather than what went wrong.
    /// </summary>
    internal static string DescribeRemediation(IReadOnlyCollection<string> blindProjects)
    {
        var names = string.Join(", ", blindProjects.OrderBy(static name => name, StringComparer.Ordinal));
        return $"No metadata references resolved for: {names}. "
             + "The compilation had no corlib, so no call graph can be derived from it. "
             + "This is a build-environment fault, not a source defect — run 'dotnet restore' on the "
             + "solution and confirm the required SDK and target frameworks are installed, then re-index.";
    }
}
