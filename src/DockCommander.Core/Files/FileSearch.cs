namespace DockCommander.Core.Files;

public sealed record FileSearchHit(string Name, string FullPath, string ParentDirectory, bool IsDirectory, long? Size);

public sealed record FileSearchOutcome(IReadOnlyList<FileSearchHit> Hits, int SkippedDirectories, bool Cancelled);

public static class FileSearch
{
    public static async Task<FileSearchOutcome> SearchAsync(IFileSystemProvider provider, string directory, string pattern,
        Action<FileSearchHit>? onFound = null, CancellationToken cancellationToken = default)
    {
        var hits = new List<FileSearchHit>();
        try
        {
            var skipped = await ProviderTreeWalker.WalkAsync(provider, directory, (entry, parent, _) =>
            {
                if (!NameMask.Matches(pattern, entry.Name, provider.PathComparer)) return ValueTask.CompletedTask;
                var hit = new FileSearchHit(entry.Name, entry.FullPath, parent, entry.IsDirectory, entry.IsDirectory ? null : entry.Size);
                hits.Add(hit);
                onFound?.Invoke(hit);
                return ValueTask.CompletedTask;
            }, cancellationToken);
            return new(hits, skipped, false);
        }
        catch (OperationCanceledException)
        {
            return new(hits, 0, true);
        }
    }
}
