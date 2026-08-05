namespace Lurp.Workspace;

internal interface IContextTierBuilder
{
    string Name { get; }
    List<CapsuleItem> Build();

    string? EmptyReason => null;
}
