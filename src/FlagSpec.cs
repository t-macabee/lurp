namespace Lurp;

/// <summary>How a flag is required. Documentation data: the handlers enforce it.</summary>
internal enum FlagRequirement { Optional, Required, Conditional }

/// <summary>
///     One CLI flag of one mode. <see cref="Description" /> is Markdown (backticks for flag names
///     and values) on one line; <c>--help</c> prints it without the backticks and the docs table
///     prints it as written. A default value is part of the description, not a field.
/// </summary>
internal sealed record FlagSpec(
    string Name,
    string Description,
    string? ValueHint = null,
    IReadOnlyList<string>? AllowedValues = null,
    FlagRequirement Requirement = FlagRequirement.Optional,
    string? EnvironmentVariable = null,
    bool Repeatable = false)
{
    /// <summary>True for a valued flag (the name ends with '=').</summary>
    internal bool TakesValue => Name.EndsWith('=');
}
