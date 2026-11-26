namespace DockCommander.Core.Files;

public sealed record FolderSize(long Bytes, bool Partial);

public sealed class FolderSizeGate
{
    private string? _path;

    public bool TryBegin(string path, StringComparer comparer)
    {
        if (_path is not null && comparer.Equals(_path, path)) return false;
        _path = path;
        return true;
    }

    public void End() => _path = null;
}

public static class FolderSizeCalculator
{
    public static async Task<FolderSize> MeasureAsync(IFileSystemProvider provider, string directory,
        CancellationToken cancellationToken = default)
    {
        long bytes = 0;
        var partial = false;
        var skipped = await ProviderTreeWalker.WalkAsync(provider, directory, async (entry, _, token) =>
        {
            if (entry.IsDirectory) return;
            if (entry.Size is long size)
            {
                bytes += size;
                return;
            }
            try
            {
                var again = await provider.GetEntryAsync(entry.FullPath, token);
                if (again?.Size is long recovered) bytes += recovered;
                else partial = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { partial = true; }
        }, cancellationToken);
        return new(bytes, partial || skipped > 0);
    }
}
