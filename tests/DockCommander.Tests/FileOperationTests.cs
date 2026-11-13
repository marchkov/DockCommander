using DockCommander.Core.Files;
using DockCommander.Core.Operations;
using DockCommander.App.Platform;
using Xunit;

// These short filesystem tests intentionally exercise the optional-token API;
// cancellation behavior has its own explicitly controlled token test.
#pragma warning disable xUnit1051

namespace DockCommander.Tests;

public sealed class ScratchDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "DockCommander.Tests", Guid.NewGuid().ToString("N"));
    public ScratchDirectory() => Directory.CreateDirectory(Root);
    public string Folder(string relative) { var path = Path.GetFullPath(Path.Combine(Root, relative)); Directory.CreateDirectory(path); return path; }
    public string File(string relative, string content = "source")
    {
        var path = Path.GetFullPath(Path.Combine(Root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }
    public void Dispose()
    {
        var boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DockCommander.Tests")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Root);
        if (!target.StartsWith(boundary, StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe cleanup path.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }
}

public sealed class FileOperationTests
{
    [Fact]
    public async Task BatchProgressCountsNestedFilesAndSkippedFolderWithoutCountingItAsCopied()
    {
        using var temp = new ScratchDirectory();
        var from = temp.Folder("from"); var to = temp.Folder("to");
        temp.File("from/tree/a.txt", "1234"); temp.File("from/tree/skip/b.txt", "123456");
        temp.File("from/tree/skip/c.txt", "12"); temp.Folder("to/tree/skip");
        var provider = new LocalFileSystemProvider();
        var reports = new List<BatchTransferProgress>();
        await new FileOperationService().TransferAsync(provider, provider, [(await provider.GetEntryAsync(Path.Combine(from, "tree")))!],
            to, false, conflict => Task.FromResult(new ConflictResolution(conflict.Source.Name == "skip" ? ConflictAction.Skip : ConflictAction.Overwrite)),
            batchProgress: new InlineProgress<BatchTransferProgress>(reports.Add));
        Assert.Contains(reports, r => r.IsScanning);
        var final = reports.Last();
        Assert.True(final.IsCompleted); Assert.Equal(3, final.TotalFiles); Assert.Equal(3, final.ProcessedFiles);
        Assert.Equal(2, final.SkippedFiles); Assert.Equal(12, final.TotalBytes); Assert.Equal(12, final.ProcessedBytes);
        Assert.Equal("1234", File.ReadAllText(Path.Combine(to, "tree/a.txt")));
        Assert.Empty(Directory.GetFiles(Path.Combine(to, "tree/skip")));
        var active = reports.Where(r => !r.IsScanning).ToArray();
        Assert.True(active.Zip(active.Skip(1)).All(pair => pair.First.ProcessedBytes <= pair.Second.ProcessedBytes));
    }

    [Fact]
    public async Task CancellingBatchScanDoesNotCreateDestination()
    {
        using var temp = new ScratchDirectory();
        var source = temp.Folder("source"); temp.File("source/a.txt");
        var to = temp.Folder("to"); var provider = new LocalFileSystemProvider();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileOperationService().TransferAsync(provider, provider,
            [new FileEntry("source", source, true, null, null)], to, false, FailOnConflict,
            cancellationToken: cancellation.Token, batchProgress: new InlineProgress<BatchTransferProgress>(_ => cancellation.Cancel())));
        Assert.Empty(Directory.GetFileSystemEntries(to));
        Assert.True(File.Exists(Path.Combine(source, "a.txt")));
    }

    [Fact]
    public async Task CancellationDuringCopyPreservesExistingDestinationAndRemovesStagingFile()
    {
        using var temp = new ScratchDirectory();
        var source = temp.File("from/large.bin");
        File.WriteAllBytes(source, new byte[1024 * 1024]);
        var destination = temp.File("to/large.bin", "keep me");
        using var cancellation = new CancellationTokenSource();
        var reports = new List<long>();
        var provider = new LocalFileSystemProvider();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.TransferFileAsync(source, destination, true, false,
            new InlineProgress<long>(bytes => { reports.Add(bytes); cancellation.Cancel(); }), cancellation.Token));
        Assert.NotEmpty(reports);
        Assert.True(reports[0] < new FileInfo(source).Length);
        Assert.Equal("keep me", File.ReadAllText(destination));
        Assert.Equal(1024 * 1024, new FileInfo(source).Length);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(destination)!));
    }

    [Fact]
    public async Task TransferReportsFileBytesAndPreservesContentAndModifiedTime()
    {
        using var temp = new ScratchDirectory();
        var source = temp.File("from/large.bin");
        var content = new byte[350000]; new Random(42).NextBytes(content); File.WriteAllBytes(source, content);
        var modified = new DateTime(2023, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, modified);
        var destination = temp.Folder("to");
        var provider = new LocalFileSystemProvider();
        var reports = new List<TransferProgress>();
        await new FileOperationService().TransferAsync(provider, provider, [(await provider.GetEntryAsync(source))!], destination,
            false, FailOnConflict, transferProgress: new InlineProgress<TransferProgress>(reports.Add));
        Assert.True(reports.Count >= 4);
        Assert.Equal(content.Length, reports.Last().BytesTransferred);
        Assert.All(reports, report => { Assert.Equal(source, report.Path); Assert.Equal(content.Length, report.FileSize); });
        Assert.True(reports.Zip(reports.Skip(1)).All(pair => pair.First.BytesTransferred <= pair.Second.BytesTransferred));
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(destination, "large.bin")));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Path.Combine(destination, "large.bin")));
    }

    private readonly LocalFileSystemProvider _provider = new();
    private readonly FileOperationService _service = new();
    private static Task<ConflictResolution> FailOnConflict(FileConflict _) => throw new InvalidOperationException("Unexpected conflict.");
    private static Task<ConflictResolution> Resolve(ConflictAction action, string? name = null) => Task.FromResult(new ConflictResolution(action, name));

    [Fact]
    public async Task ListingHasParentThenDirectoriesThenSortedFiles()
    {
        using var temp = new ScratchDirectory();
        temp.File("z.txt"); temp.File("A.txt"); temp.Folder("b"); temp.Folder("a");
        var entries = await _provider.ListAsync(temp.Root);
        Assert.Equal(new[] { "..", "a", "b", "A.txt", "z.txt" }, entries.Select(x => x.Name));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../outside")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public void ChildNamesCannotEscapeTheirDirectory(string name)
    {
        using var temp = new ScratchDirectory();
        Assert.Throws<ArgumentException>(() => _provider.CombinePath(temp.Root, name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsSameFileWithoutTouchingContent(bool move)
    {
        using var temp = new ScratchDirectory();
        var path = temp.File("a.txt");
        var entry = (await _provider.GetEntryAsync(path))!;
        await Assert.ThrowsAsync<IOException>(() => _service.TransferAsync(_provider, _provider, [entry], temp.Root, move, FailOnConflict));
        Assert.Equal("source", File.ReadAllText(path));
    }

    [Fact]
    public async Task RejectsCopyingDirectoryIntoItself()
    {
        using var temp = new ScratchDirectory();
        var source = temp.Folder("source");
        var destination = temp.Folder("source/child");
        var entry = (await _provider.GetEntryAsync(source))!;
        await Assert.ThrowsAsync<IOException>(() => _service.TransferAsync(_provider, _provider, [entry], destination, false, FailOnConflict));
        Assert.False(Directory.Exists(Path.Combine(destination, "source")));
    }

    [Fact]
    public async Task CopySkipPreservesBothFiles()
    {
        using var temp = new ScratchDirectory();
        var source = temp.File("from/a.txt", "new");
        var destination = temp.File("to/a.txt", "old");
        var result = await _service.TransferAsync(_provider, _provider, [(await _provider.GetEntryAsync(source))!],
            Path.GetDirectoryName(destination)!, false, _ => Resolve(ConflictAction.Skip));
        Assert.Equal(1, result.Skipped);
        Assert.Equal("old", File.ReadAllText(destination));
        Assert.Equal("new", File.ReadAllText(source));
    }

    [Fact]
    public async Task CopyOverwriteRequiresCallbackAndReplacesContent()
    {
        using var temp = new ScratchDirectory();
        var source = temp.File("from/a.txt", "new");
        var destination = temp.File("to/a.txt", "old");
        var asked = 0;
        await _service.TransferAsync(_provider, _provider, [(await _provider.GetEntryAsync(source))!],
            Path.GetDirectoryName(destination)!, false, _ => { asked++; return Resolve(ConflictAction.Overwrite); });
        Assert.Equal(1, asked);
        Assert.Equal("new", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, ".dc-*.tmp"));
    }

    [Fact]
    public async Task NestedRenameStaysInTheDestinationSubdirectory()
    {
        using var temp = new ScratchDirectory();
        var source = temp.Folder("from/tree");
        temp.File("from/tree/sub/a.txt", "new");
        temp.File("to/tree/sub/a.txt", "old");
        var to = temp.Folder("to");
        await _service.TransferAsync(_provider, _provider, [(await _provider.GetEntryAsync(source))!], to, false,
            conflict => conflict.Source.IsDirectory ? Resolve(ConflictAction.Overwrite) : Resolve(ConflictAction.Rename, "renamed.txt"));
        Assert.Equal("old", File.ReadAllText(Path.Combine(to, "tree/sub/a.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(to, "tree/sub/renamed.txt")));
        Assert.False(File.Exists(Path.Combine(to, "renamed.txt")));
    }

    [Fact]
    public async Task MoveWithSkippedFileKeepsSourceDirectoryAndSkippedData()
    {
        using var temp = new ScratchDirectory();
        var source = temp.Folder("from/tree");
        temp.File("from/tree/a.txt", "new"); temp.File("from/tree/b.txt", "move");
        temp.File("to/tree/a.txt", "old");
        var to = temp.Folder("to");
        var result = await _service.TransferAsync(_provider, _provider, [(await _provider.GetEntryAsync(source))!], to, true,
            conflict => Resolve(conflict.Source.IsDirectory ? ConflictAction.Overwrite : ConflictAction.Skip));
        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(Path.Combine(source, "a.txt")));
        Assert.False(File.Exists(Path.Combine(source, "b.txt")));
        Assert.Equal("move", File.ReadAllText(Path.Combine(to, "tree/b.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(to, "tree/a.txt")));
    }

    [Fact]
    public async Task MovingWholeTreeRemovesOnlyEmptiedSource()
    {
        using var temp = new ScratchDirectory();
        var source = temp.Folder("from/tree");
        temp.File("from/tree/sub/a.txt");
        var to = temp.Folder("to");
        await _service.TransferAsync(_provider, _provider, [(await _provider.GetEntryAsync(source))!], to, true, FailOnConflict);
        Assert.False(Directory.Exists(source));
        Assert.Equal("source", File.ReadAllText(Path.Combine(to, "tree/sub/a.txt")));
    }

    [Fact]
    public async Task CopySkipsALinkAndContinuesWithTheRestOfTheTree()
    {
        using var temp = new ScratchDirectory();
        var target = temp.File("target.txt", "secret");
        var source = temp.Folder("from/tree");
        var file = temp.File("from/tree/a.txt", "kept");
        var link = Path.Combine(source, "link.txt");
        if (!TryCreateLink(link, target)) return;
        var to = temp.Folder("to");
        var reports = new List<BatchTransferProgress>();
        try
        {
            var result = await _service.TransferAsync(_provider, _provider, [(await _provider.GetEntryAsync(source))!], to, false, FailOnConflict,
                batchProgress: new InlineProgress<BatchTransferProgress>(reports.Add));
            Assert.Equal(1, result.Skipped);
            Assert.Equal("kept", File.ReadAllText(Path.Combine(to, "tree/a.txt")));
            Assert.False(File.Exists(Path.Combine(to, "tree/link.txt")));
            Assert.Equal("kept", File.ReadAllText(file));
            Assert.Equal("secret", File.ReadAllText(target));
            Assert.Contains(reports, report => report.IsCompleted && report.SkippedFiles == 1);
        }
        finally { if (File.Exists(link)) File.Delete(link); }
    }

    [Fact]
    public async Task MoveRenamesADirectoryAndKeepsAnInnerLink()
    {
        using var temp = new ScratchDirectory();
        var target = temp.File("target.txt", "secret");
        var source = temp.Folder("from/tree");
        temp.File("from/tree/a.txt", "kept");
        var link = Path.Combine(source, "link.txt");
        if (!TryCreateLink(link, target)) return;
        var to = temp.Folder("to");
        var movedLink = Path.Combine(to, "tree", "link.txt");
        try
        {
            var result = await _service.TransferAsync(_provider, _provider, [(await _provider.GetEntryAsync(source))!], to, true, FailOnConflict);
            Assert.Equal(1, result.Completed);
            Assert.Equal(0, result.Skipped);
            Assert.False(Directory.Exists(source));
            Assert.Equal("kept", File.ReadAllText(Path.Combine(to, "tree/a.txt")));
            Assert.True(File.GetAttributes(movedLink).HasFlag(FileAttributes.ReparsePoint));
            Assert.Equal("secret", File.ReadAllText(target));
        }
        finally
        {
            if (File.Exists(movedLink)) File.Delete(movedLink);
            if (File.Exists(link)) File.Delete(link);
        }
    }

    private static bool TryCreateLink(string link, string target)
    {
        try { File.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
    }

    [Fact]
    public async Task ConflictCancellationLeavesExistingDataUntouched()
    {
        using var temp = new ScratchDirectory();
        var source = temp.File("from/a.txt", "new");
        var destination = temp.File("to/a.txt", "old");
        var entry = (await _provider.GetEntryAsync(source))!;
        await Assert.ThrowsAsync<OperationCanceledException>(() => _service.TransferAsync(_provider, _provider,
            [entry], Path.GetDirectoryName(destination)!, true,
            _ => Resolve(ConflictAction.Cancel)));
        Assert.Equal("old", File.ReadAllText(destination));
        Assert.Equal("new", File.ReadAllText(source));
    }

    [Fact]
    public async Task FailedOverwriteDoesNotTruncateDestination()
    {
        using var temp = new ScratchDirectory();
        var source = temp.File("from/a.txt", "new");
        var destination = temp.File("to/a.txt", "old");
        using var locked = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAnyAsync<IOException>(() => _provider.CopyFileAsync(source, destination, true));
        Assert.Equal("old", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, ".dc-*.tmp"));
    }

    [Fact]
    public async Task FileCannotOverwriteDirectory()
    {
        using var temp = new ScratchDirectory();
        var source = temp.File("from/a");
        temp.File("to/a/valuable.txt", "keep");
        var to = temp.Folder("to");
        var entry = (await _provider.GetEntryAsync(source))!;
        await Assert.ThrowsAsync<IOException>(() => _service.TransferAsync(_provider, _provider,
            [entry], to, false, _ => Resolve(ConflictAction.Overwrite)));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(to, "a/valuable.txt")));
    }

    [Fact]
    public async Task RecycleUnavailableNeverFallsBackToDeletion()
    {
        using var temp = new ScratchDirectory();
        var path = temp.File("keep.txt");
        var entry = (await _provider.GetEntryAsync(path))!;
        await Assert.ThrowsAsync<NotSupportedException>(() => _service.DeleteAsync(_provider, [entry], DeleteMode.Recycle));
        Assert.True(File.Exists(path));
        await Assert.ThrowsAsync<NotSupportedException>(() => _provider.DeleteAsync(path, DeleteMode.Recycle));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task RecycleAndPermanentDeletionTakeDifferentPaths()
    {
        using var temp = new ScratchDirectory();
        var trash = new RecordingTrash();
        var provider = new LocalFileSystemProvider(trash);
        var path = temp.File("delete.txt");
        var entry = (await provider.GetEntryAsync(path))!;
        await _service.DeleteAsync(provider, [entry], DeleteMode.Recycle);
        Assert.Equal(path, Assert.Single(trash.Paths));
        Assert.True(File.Exists(path));
        await _service.DeleteAsync(provider, [entry], DeleteMode.Permanent);
        Assert.False(File.Exists(path));
        Assert.Single(trash.Paths);
    }

    [Fact]
    public async Task CancellationBeforeTransferDoesNotCreateDestination()
    {
        using var temp = new ScratchDirectory();
        var source = temp.File("from/a.txt");
        var to = temp.Folder("to");
        var entry = (await _provider.GetEntryAsync(source))!;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.TransferAsync(_provider, _provider, [entry], to, false, FailOnConflict,
            cancellationToken: cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(to));
    }

    [Fact]
    public async Task CreateDirectoryRejectsAnExistingName()
    {
        using var temp = new ScratchDirectory();
        await _service.CreateDirectoryAsync(_provider, temp.Root, "new");
        await Assert.ThrowsAsync<IOException>(() => _service.CreateDirectoryAsync(_provider, temp.Root, "new"));
    }

    [Fact]
    public async Task PermanentDeleteRemovesADirectoryTree()
    {
        using var temp = new ScratchDirectory();
        var file = temp.File("dir/a.txt", "gone");
        var directory = Path.GetDirectoryName(file)!;
        var entry = (await _provider.GetEntryAsync(directory))!;
        await _service.DeleteAsync(_provider, [entry], DeleteMode.Permanent);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task PermanentDeleteStopsBeforeRemovingAnythingWhenATreeContainsALink()
    {
        using var temp = new ScratchDirectory();
        var keep = temp.File("dir/keep.txt", "keep");
        var sibling = temp.File("sibling.txt", "sibling");
        var link = Path.Combine(Path.GetDirectoryName(keep)!, "link.txt");
        try { File.CreateSymbolicLink(link, keep); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }
        try
        {
            var directory = (await _provider.GetEntryAsync(Path.GetDirectoryName(keep)!))!;
            var extra = (await _provider.GetEntryAsync(sibling))!;
            var error = await Assert.ThrowsAsync<IOException>(() => _service.DeleteAsync(_provider, [extra, directory], DeleteMode.Permanent));
            Assert.Contains("was not started", error.Message);
            Assert.Contains(link, error.Message);
            Assert.DoesNotContain("Already deleted", error.Message);
            Assert.Equal("keep", File.ReadAllText(keep));
            Assert.Equal("sibling", File.ReadAllText(sibling));
        }
        finally { if (File.Exists(link)) File.Delete(link); }
    }

    [Fact]
    public async Task PermanentDeleteStopsBeforeRemovingAnythingWhenAWindowsFileIsReadOnly()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new ScratchDirectory();
        var keep = temp.File("dir/keep.txt", "keep");
        var blocked = temp.File("dir/locked.txt", "locked");
        var sibling = temp.File("sibling.txt", "sibling");
        File.SetAttributes(blocked, FileAttributes.ReadOnly);
        try
        {
            var directory = (await _provider.GetEntryAsync(Path.GetDirectoryName(keep)!))!;
            var extra = (await _provider.GetEntryAsync(sibling))!;
            var error = await Assert.ThrowsAsync<IOException>(() => _service.DeleteAsync(_provider, [extra, directory], DeleteMode.Permanent));
            Assert.Contains("was not started", error.Message);
            Assert.Contains(blocked, error.Message);
            Assert.Equal("keep", File.ReadAllText(keep));
            Assert.Equal("sibling", File.ReadAllText(sibling));
        }
        finally { File.SetAttributes(blocked, FileAttributes.Normal); }
    }

    [Fact]
    public async Task PermanentDeleteClearsWindowsDirectoryReadOnlyAndRemovesTheTree()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new ScratchDirectory();
        var file = temp.File("dir/a.txt", "gone");
        var directory = Path.GetDirectoryName(file)!;
        var attributes = File.GetAttributes(directory);
        File.SetAttributes(directory, attributes | FileAttributes.ReadOnly);
        try
        {
            var entry = (await _provider.GetEntryAsync(directory))!;
            await _service.DeleteAsync(_provider, [entry], DeleteMode.Permanent);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) File.SetAttributes(directory, attributes);
        }
    }

    [Fact]
    public async Task PermanentDeleteNamesEntriesRemovedBeforeALockedFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new ScratchDirectory();
        var first = temp.File("dir/a.txt", "gone");
        var locked = temp.File("dir/m.txt", "stay");
        using var stream = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var directory = (await _provider.GetEntryAsync(Path.GetDirectoryName(first)!))!;
        var error = await Assert.ThrowsAsync<IOException>(() => _service.DeleteAsync(_provider, [directory], DeleteMode.Permanent));
        Assert.Contains("Already deleted:", error.Message);
        Assert.Contains(first, error.Message);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(locked));
        Assert.True(Directory.Exists(Path.GetDirectoryName(first)!));
        _ = stream.Length;
    }

    [Fact]
    public async Task DeleteValidatesEveryEntryBeforeRemovingAny()
    {
        var provider = new ScriptedDeleteProvider();
        var error = await Assert.ThrowsAsync<IOException>(() => _service.DeleteAsync(provider,
            [new FileEntry("ok", "ok", false, null, null), new FileEntry("blocked", "blocked", false, null, null)], DeleteMode.Permanent));
        Assert.Contains("was not started", error.Message);
        Assert.Empty(provider.Deleted);
    }

    [Fact]
    public async Task DeleteReportsEntriesRemovedBeforeALaterFailure()
    {
        var provider = new ScriptedDeleteProvider();
        var error = await Assert.ThrowsAsync<IOException>(() => _service.DeleteAsync(provider,
            [new FileEntry("ok", "ok", false, null, null), new FileEntry("locked", "locked", false, null, null)], DeleteMode.Permanent));
        Assert.Contains("Already deleted: ok", error.Message);
        Assert.Equal("ok", Assert.Single(provider.Deleted));
    }

    [Fact]
    public void WindowsRecycleSinkRejectsPermanentDeleteFallback()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sink = new WindowsTrashService.RecycleProgressSink(CancellationToken.None);
        Assert.True(sink.PreDeleteItem(0, IntPtr.Zero) < 0);
        Assert.True(sink.RefusedPermanentDelete);
        var allowed = new WindowsTrashService.RecycleProgressSink(CancellationToken.None);
        Assert.Equal(0, allowed.PreDeleteItem(0x80, IntPtr.Zero));
    }

    private sealed class ScriptedDeleteProvider : IFileSystemProvider
    {
        public List<string> Deleted { get; } = [];
        public string Id => "scripted";
        public string DisplayName => "Scripted";
        public bool SupportsRecycle => true;
        public StringComparer PathComparer => StringComparer.Ordinal;
        public bool CanHandle(string path) => true;
        public string NormalizePath(string path) => path;
        public string CombinePath(string directory, string name) => directory;
        public bool IsSameOrDescendant(string path, string ancestor) => false;
        public Task<IReadOnlyList<LocationEntry>> GetLocationsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveEmptyDirectoryAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MoveFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ValidateDeleteAsync(string path, DeleteMode mode, CancellationToken cancellationToken = default) =>
            path == "blocked" ? Task.FromException(new IOException("Permanent deletion was not started.")) : Task.CompletedTask;
        public Task DeleteAsync(string path, DeleteMode mode, CancellationToken cancellationToken = default)
        {
            if (path == "locked") throw new IOException("The file is in use.");
            Deleted.Add(path);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTrash : ITrashService
    {
        public bool IsSupported => true;
        public List<string> Paths { get; } = [];
        public Task RecycleAsync(string path, CancellationToken cancellationToken = default) { Paths.Add(path); return Task.CompletedTask; }
    }
}
