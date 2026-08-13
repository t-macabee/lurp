using System;

namespace Lurp.Shared;

internal static class FqnNormalizer
{
    public static string NormalizeForCommand(string fqn)
    {
        if (string.IsNullOrEmpty(fqn))
            return fqn;

        return fqn.StartsWith("global::", StringComparison.Ordinal)
            ? fqn["global::".Length..]
            : fqn;
    }
}