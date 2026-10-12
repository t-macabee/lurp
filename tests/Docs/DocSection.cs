namespace Lurp.Tests.Docs;

internal sealed record DocSection(string RelativePath, string Key, Func<string> Render);
