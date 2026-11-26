namespace DockCommander.Core.Files;

public static class ProviderTreeWalker
{
    public static async Task<int> WalkAsync(IFileSystemProvider provider, string root,
        Func<FileEntry, string, CancellationToken, ValueTask> visit, CancellationToken cancellationToken = default)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var skipped = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IReadOnlyList<FileEntry> children;
            try
            {
                children = await provider.ListAsync(directory, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                skipped++;
                continue;
            }
            foreach (var entry in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Name == ".." || entry.IsLink) continue;
                await visit(entry, directory, cancellationToken);
                if (entry.IsDirectory) pending.Push(entry.FullPath);
            }
        }
        return skipped;
    }
}
