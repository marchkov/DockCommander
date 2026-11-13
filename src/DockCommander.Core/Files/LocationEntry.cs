namespace DockCommander.Core.Files;

public sealed record LocationEntry(string Id, string DisplayName, string RootPath, string? Kind = null);
