using DockCommander.Core.Files;

namespace DockCommander.Core.Operations;

public enum ConflictAction { Cancel, Overwrite, Skip, Rename }
public sealed record ConflictResolution(ConflictAction Action, string? NewName = null);
public sealed record FileConflict(FileEntry Source, FileEntry Destination);
public sealed record OperationResult(int Completed, int Skipped);
public sealed record TransferProgress(string Path, long BytesTransferred, long? FileSize);
public sealed record BatchTransferProgress(string Path, long ProcessedBytes, long? TotalBytes,
    int ProcessedFiles, int TotalFiles, int SkippedFiles, bool IsScanning = false, bool IsCompleted = false);

public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

public sealed class FileOperationService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> RenameAsync(IFileSystemProvider provider, FileEntry entry, string newName,
        CancellationToken cancellationToken = default)
    {
        if (entry.Name == "..") throw new IOException("The parent entry cannot be renamed.");
        if (entry.IsLink) throw new IOException("Renaming symbolic links and junctions is not supported yet.");
        await _gate.WaitAsync(cancellationToken);
        try { return await provider.RenameAsync(entry.FullPath, newName, cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task<OperationResult> TransferAsync(IFileSystemProvider sourceProvider, IFileSystemProvider destinationProvider,
        IReadOnlyList<FileEntry> entries, string destinationDirectory, bool move,
        Func<FileConflict, Task<ConflictResolution>> resolveConflict, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, IProgress<TransferProgress>? transferProgress = null,
        IProgress<BatchTransferProgress>? batchProgress = null)
    {
        if (!ReferenceEquals(sourceProvider, destinationProvider))
            throw new NotSupportedException("Transfers between different providers are not supported yet.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var completed = 0;
            var skipped = 0;
            var provider = sourceProvider;
            var children = new Dictionary<string, FileEntry[]>(provider.PathComparer);
            var totals = new Dictionary<string, (long Bytes, int Files)>(provider.PathComparer);
            long totalBytes = 0, processedBytes = 0;
            int totalFiles = 0, processedFiles = 0, skippedFiles = 0;
            var sizesKnown = true;
            if (batchProgress is not null)
            {
                foreach (var entry in entries.Where(x => x.Name != ".."))
                {
                    var sum = await Scan(entry);
                    totalBytes += sum.Bytes; totalFiles += sum.Files;
                }
                ReportBatch("");
            }
            foreach (var entry in entries.Where(x => x.Name != ".."))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await TransferEntry(entry, destinationDirectory, entry.Name)) completed++;
                else skipped++;
            }
            ReportBatch("", completed: true);
            return new(completed, skipped);

            void ReportBatch(string path, long currentBytes = 0, bool completed = false) =>
                batchProgress?.Report(new(path, processedBytes + currentBytes, sizesKnown ? totalBytes : null,
                    processedFiles, totalFiles, skippedFiles, IsCompleted: completed));

            async Task<(long Bytes, int Files)> Scan(FileEntry entry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batchProgress!.Report(new(entry.FullPath, 0, null, 0, 0, 0, IsScanning: true));
                // Count the link itself and do not walk its target.
                if (entry.IsLink)
                {
                    totals[entry.FullPath] = (0, 1);
                    return (0, 1);
                }
                (long Bytes, int Files) sum = (Math.Max(0, entry.Size ?? 0), 1);
                if (entry.IsDirectory)
                {
                    var listed = (await provider.ListAsync(entry.FullPath, cancellationToken)).Where(e => e.Name != "..").ToArray();
                    children[entry.FullPath] = listed;
                    sum = (0, 0);
                    foreach (var child in listed)
                    {
                        var childSum = await Scan(child);
                        sum.Bytes += childSum.Bytes; sum.Files += childSum.Files;
                    }
                }
                else if (entry.Size is null) sizesKnown = false;
                totals[entry.FullPath] = sum;
                return sum;
            }

            async Task<bool> TransferEntry(FileEntry source, string destinationParent, string name)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (source.IsLink)
                {
                    Account(source, skipped: true);
                    return false;
                }
                var destination = provider.CombinePath(destinationParent, name);
                CheckDestination();
                var overwrite = false;
                while (await provider.GetEntryAsync(destination, cancellationToken) is { } existing)
                {
                    if (existing.IsLink)
                    {
                        Account(source, skipped: true);
                        return false;
                    }
                    var resolution = await resolveConflict(new(source, existing));
                    switch (resolution.Action)
                    {
                        case ConflictAction.Cancel: throw new OperationCanceledException("Operation cancelled.");
                        case ConflictAction.Skip:
                            Account(source, skipped: true);
                            return false;
                        case ConflictAction.Rename:
                            destination = provider.CombinePath(destinationParent, resolution.NewName ?? "");
                            CheckDestination();
                            continue;
                        case ConflictAction.Overwrite:
                            if (source.IsDirectory != existing.IsDirectory)
                                throw new IOException("A file cannot replace a folder (or vice versa). Choose Rename or Skip.");
                            overwrite = true;
                            break;
                        default: throw new InvalidOperationException("Unknown conflict resolution.");
                    }
                    break;
                }
                if (move && source.IsDirectory && !overwrite &&
                    await provider.TryMoveDirectoryAsync(source.FullPath, destination, cancellationToken))
                {
                    Account(source, skipped: false);
                    return true;
                }
                progress?.Report($"{(move ? "Moving" : "Copying")}: {source.Name}");
                if (!source.IsDirectory)
                {
                    transferProgress?.Report(new(source.FullPath, 0, source.Size));
                    ReportBatch(source.FullPath);
                    await provider.TransferFileAsync(source.FullPath, destination, overwrite, move,
                        new InlineProgress<long>(bytes =>
                        {
                            transferProgress?.Report(new(source.FullPath, bytes, source.Size));
                            ReportBatch(source.FullPath, Math.Clamp(bytes, 0, Math.Max(0, source.Size ?? 0)));
                        }), cancellationToken);
                    processedBytes += Math.Max(0, source.Size ?? 0); processedFiles++;
                    ReportBatch(source.FullPath);
                    transferProgress?.Report(new(source.FullPath, source.Size ?? 0, source.Size));
                    return true;
                }
                await provider.CreateDirectoryAsync(destination, cancellationToken);
                var allTransferred = true;
                var listed = children.TryGetValue(source.FullPath, out var planned) ? planned :
                    (await provider.ListAsync(source.FullPath, cancellationToken)).Where(x => x.Name != "..").ToArray();
                foreach (var child in listed)
                    allTransferred &= await TransferEntry(child, destination, child.Name);
                if (move && allTransferred) await provider.RemoveEmptyDirectoryAsync(source.FullPath, cancellationToken);
                return allTransferred;

                void CheckDestination()
                {
                    if (provider.IsSameOrDescendant(destination, source.FullPath))
                        throw new IOException("The destination must not be the source or a folder inside it.");
                }

                void Account(FileEntry entry, bool skipped)
                {
                    if (!totals.TryGetValue(entry.FullPath, out var total)) return;
                    processedBytes += total.Bytes; processedFiles += total.Files;
                    if (skipped) skippedFiles += total.Files;
                    ReportBatch(entry.FullPath);
                }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(IFileSystemProvider provider, IReadOnlyList<FileEntry> entries, DeleteMode mode,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (mode == DeleteMode.Recycle && !provider.SupportsRecycle)
            throw new NotSupportedException("The recycle bin is unavailable. Nothing was deleted. Shift+F8 explicitly deletes permanently.");
        await _gate.WaitAsync(cancellationToken);
        var deleted = new List<string>();
        try
        {
            var targets = entries.Where(x => x.Name != "..").ToArray();
            foreach (var entry in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await provider.ValidateDeleteAsync(entry.FullPath, mode, cancellationToken);
            }
            foreach (var entry in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"{(mode == DeleteMode.Recycle ? "Recycling" : "Deleting")}: {entry.Name}");
                await provider.DeleteAsync(entry.FullPath, mode, cancellationToken);
                deleted.Add(entry.Name);
            }
        }
        catch (Exception ex) when (deleted.Count > 0)
        {
            throw DeleteFailures.WithDeleted(ex, deleted);
        }
        finally { _gate.Release(); }
    }

    public async Task CreateDirectoryAsync(IFileSystemProvider provider, string directory, string name, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = provider.CombinePath(directory, name);
            if (await provider.GetEntryAsync(path, cancellationToken) is not null) throw new IOException("An entry with this name already exists.");
            await provider.CreateDirectoryAsync(path, cancellationToken);
        }
        finally { _gate.Release(); }
    }
}

internal static class DeleteFailures
{
    internal static string Format(IReadOnlyList<string> paths)
    {
        var listed = string.Join(", ", paths.Take(8));
        return paths.Count <= 8 ? listed : $"{listed}, and {paths.Count - 8} more";
    }

    internal static string Blocked(IReadOnlyList<string> blockers) =>
        $"Permanent deletion was not started. These items block it: {Format(blockers)}.";

    internal static Exception WithDeleted(Exception exception, IReadOnlyList<string> deleted)
    {
        var message = $"{exception.Message} Already deleted: {Format(deleted)}.";
        return exception is OperationCanceledException
            ? new OperationCanceledException(message, exception)
            : new IOException(message, exception);
    }
}
