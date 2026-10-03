using Microsoft.Build.Locator;

namespace Lurp.Workspace;

internal static class MSBuildRegistration
{
    internal const string NoSdkMessage =
        "Indexing requires a .NET SDK that can build the target solution. Install a .NET SDK, restore the solution, and retry.";

    internal static VisualStudioInstance? RegisterOrThrow()
    {
        if (MSBuildLocator.IsRegistered) return null;

        List<VisualStudioInstance> instances;
        try
        {
            instances = MSBuildLocator.QueryVisualStudioInstances().ToList();
        }
        catch (Exception)
        {
            throw new WorkspaceUnreadableException(NoSdkMessage);
        }

        if (instances.Count == 0) throw new WorkspaceUnreadableException(NoSdkMessage);

        return MSBuildLocator.RegisterDefaults();
    }
}
