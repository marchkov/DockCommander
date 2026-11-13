namespace DockCommander.Core.Files;

public sealed record FileEntry(string Name, string FullPath, bool IsDirectory, long? Size,
    DateTimeOffset? Modified, string? Extension = null, bool IsLink = false);
