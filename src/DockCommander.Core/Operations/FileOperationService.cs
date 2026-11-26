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
        // The transfer queue runs one copy or move at a time. Delete, rename, and folder
        // creation stay on _gate and may overlap a transfer; they do not wait for it.
        var completed = 0;
        var skipped = 0;
        var sameProvider = ReferenceEquals(sourceProvider, destinationProvider);
        var children = new Dictionary<string, FileEntry[]>(sourceProvider.PathComparer);
        var totals = new Dictionary<string, (long Bytes, int Files)>(sourceProvider.PathComparer);
        long totalBytes = 0, processedBytes = 0;
        int totalFiles = 0, processedFiles = 0, skippedFiles = 0;
        var sizesKnown = true;
        var roots = entries.Where(entry => entry.Name != "..").ToArray();
        if (batchProgress is not null)
        {
            await Scan(roots);
            foreach (var entry in roots)
            {
                var sum = totals[entry.FullPath];
                totalBytes += sum.Bytes;
                totalFiles += sum.Files;
            }
            ReportBatch("");
        }
        var pending = new Stack<TransferStep>();
        for (var index = roots.Length - 1; index >= 0; index--)
            pending.Push(new TransferStep(roots[index], destinationDirectory, roots[index].Name));
        while (pending.Count > 0)
        {
            var step = pending.Pop();
            if (step.FinishDirectory)
            {
                if (move && step.AllTransferred)
                    await sourceProvider.RemoveEmptyDirectoryAsync(step.Source.FullPath, cancellationToken);
                Finish(step);
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (step.Source.IsLink)
            {
                Account(step.Source, skipped: true);
                step.AllTransferred = false;
                Finish(step);
                continue;
            }
            var destination = destinationProvider.CombinePath(step.DestinationParent, step.Name);
            CheckDestination(step.Source, destination);
            var overwrite = false;
            var blocked = false;
            while (await destinationProvider.GetEntryAsync(destination, cancellationToken) is { } existing)
            {
                if (existing.IsLink)
                {
                    Account(step.Source, skipped: true);
                    blocked = true;
                    break;
                }
                var resolution = await resolveConflict(new(step.Source, existing));
                switch (resolution.Action)
                {
                    case ConflictAction.Cancel: throw new OperationCanceledException("Operation cancelled.");
                    case ConflictAction.Skip:
                        Account(step.Source, skipped: true);
                        blocked = true;
                        break;
                    case ConflictAction.Rename:
                        destination = destinationProvider.CombinePath(step.DestinationParent, resolution.NewName ?? "");
                        CheckDestination(step.Source, destination);
                        continue;
                    case ConflictAction.Overwrite:
                        if (step.Source.IsDirectory != existing.IsDirectory)
                            throw new IOException("A file cannot replace a folder (or vice versa). Choose Rename or Skip.");
                        overwrite = true;
                        break;
                    default: throw new InvalidOperationException("Unknown conflict resolution.");
                }
                break;
            }
            if (blocked)
            {
                step.AllTransferred = false;
                Finish(step);
                continue;
            }
            if (sameProvider && move && step.Source.IsDirectory && !overwrite &&
                await sourceProvider.TryMoveDirectoryAsync(step.Source.FullPath, destination, cancellationToken))
            {
                Account(step.Source, skipped: false);
                Finish(step);
                continue;
            }
            progress?.Report($"{(move ? "Moving" : "Copying")}: {step.Source.Name}");
            if (!step.Source.IsDirectory)
            {
                transferProgress?.Report(new(step.Source.FullPath, 0, step.Source.Size));
                ReportBatch(step.Source.FullPath);
                var fileProgress = new InlineProgress<long>(bytes =>
                {
                    transferProgress?.Report(new(step.Source.FullPath, bytes, step.Source.Size));
                    ReportBatch(step.Source.FullPath, Math.Clamp(bytes, 0, Math.Max(0, step.Source.Size ?? 0)));
                });
                if (sameProvider)
                    await sourceProvider.TransferFileAsync(step.Source.FullPath, destination, overwrite, move, fileProgress, cancellationToken);
                else
                {
                    await using (var input = await sourceProvider.OpenReadAsync(step.Source.FullPath, cancellationToken))
                        await destinationProvider.WriteFileAsync(destination, input, overwrite, fileProgress, cancellationToken);
                    if (move) await sourceProvider.DeleteAsync(step.Source.FullPath, DeleteMode.Permanent, cancellationToken);
                }
                processedBytes += Math.Max(0, step.Source.Size ?? 0);
                processedFiles++;
                ReportBatch(step.Source.FullPath);
                transferProgress?.Report(new(step.Source.FullPath, step.Source.Size ?? 0, step.Source.Size));
                Finish(step);
                continue;
            }
            await destinationProvider.CreateDirectoryAsync(destination, cancellationToken);
            var listed = children.TryGetValue(step.Source.FullPath, out var planned) ? planned :
                (await sourceProvider.ListAsync(step.Source.FullPath, cancellationToken)).Where(entry => entry.Name != "..").ToArray();
            var directory = new TransferStep(step.Source, step.DestinationParent, step.Name)
            {
                FinishDirectory = true,
                Parent = step.Parent
            };
            pending.Push(directory);
            for (var index = listed.Length - 1; index >= 0; index--)
                pending.Push(new TransferStep(listed[index], destination, listed[index].Name) { Parent = directory });
        }
        ReportBatch("", completed: true);
        return new(completed, skipped);

        void ReportBatch(string path, long currentBytes = 0, bool completed = false) =>
            batchProgress?.Report(new(path, processedBytes + currentBytes, sizesKnown ? totalBytes : null,
                processedFiles, totalFiles, skippedFiles, IsCompleted: completed));

        async Task Scan(IReadOnlyList<FileEntry> selected)
        {
            var stack = new Stack<(FileEntry Entry, bool ChildrenDone)>();
            for (var index = selected.Count - 1; index >= 0; index--) stack.Push((selected[index], false));
            while (stack.Count > 0)
            {
                var (entry, childrenDone) = stack.Pop();
                cancellationToken.ThrowIfCancellationRequested();
                if (!childrenDone)
                {
                    batchProgress!.Report(new(entry.FullPath, 0, null, 0, 0, 0, IsScanning: true));
                    if (entry.IsLink)
                    {
                        totals[entry.FullPath] = (0, 1);
                        continue;
                    }
                    if (!entry.IsDirectory)
                    {
                        if (entry.Size is null) sizesKnown = false;
                        totals[entry.FullPath] = (Math.Max(0, entry.Size ?? 0), 1);
                        continue;
                    }
                    var listed = (await sourceProvider.ListAsync(entry.FullPath, cancellationToken)).Where(entry => entry.Name != "..").ToArray();
                    children[entry.FullPath] = listed;
                    stack.Push((entry, true));
                    for (var index = listed.Length - 1; index >= 0; index--) stack.Push((listed[index], false));
                    continue;
                }
                long bytes = 0;
                var files = 0;
                foreach (var child in children[entry.FullPath])
                {
                    var childTotal = totals[child.FullPath];
                    bytes += childTotal.Bytes;
                    files += childTotal.Files;
                }
                totals[entry.FullPath] = (bytes, files);
            }
        }

        void CheckDestination(FileEntry source, string destination)
        {
            if (sourceProvider.CanHandle(destination) && sourceProvider.IsSameOrDescendant(destination, source.FullPath))
                throw new IOException("The destination must not be the source or a folder inside it.");
        }

        void Account(FileEntry entry, bool skipped)
        {
            if (!totals.TryGetValue(entry.FullPath, out var total)) return;
            processedBytes += total.Bytes;
            processedFiles += total.Files;
            if (skipped) skippedFiles += total.Files;
            ReportBatch(entry.FullPath);
        }

        void Finish(TransferStep step)
        {
            if (step.Parent is { } parent) parent.AllTransferred &= step.AllTransferred;
            else if (step.AllTransferred) completed++;
            else skipped++;
        }
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

    public async Task<string> CreateFileAsync(IFileSystemProvider provider, string directory, string name, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = provider.CombinePath(directory, name);
            if (await provider.GetEntryAsync(path, cancellationToken) is not null) throw new IOException("An entry with this name already exists.");
            await using var empty = new MemoryStream([]);
            await provider.WriteFileAsync(path, empty, false, null, cancellationToken);
            return path;
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

    internal static PartialDeletionException WithDeleted(Exception exception, IReadOnlyList<string> deleted)
    {
        var message = $"{exception.Message} Already deleted: {Format(deleted)}.";
        return new PartialDeletionException(message, deleted, exception);
    }
}

public sealed class PartialDeletionException : IOException
{
    public PartialDeletionException(string message, IReadOnlyList<string> deleted, Exception inner)
        : base(message, inner) => Deleted = deleted;

    public IReadOnlyList<string> Deleted { get; }
}

sealed class TransferStep(FileEntry source, string destinationParent, string name)
{
    public FileEntry Source { get; } = source;
    public string DestinationParent { get; } = destinationParent;
    public string Name { get; } = name;
    public bool FinishDirectory { get; init; }
    public TransferStep? Parent { get; init; }
    public bool AllTransferred { get; set; } = true;
}
