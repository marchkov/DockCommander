using DockCommander.Core.Files;
using DockCommander.Core.Operations;
using Xunit;

namespace DockCommander.Tests;

public sealed class TransferQueueTests
{
    [Fact]
    public async Task SecondTransferWaitsUntilTheFirstFinishes()
    {
        await using var queue = new TransferQueue(new FileOperationService());
        var provider = new HoldingProvider();
        queue.Enqueue(Request(provider, "a.txt"));
        queue.Enqueue(Request(provider, "b.txt"));
        await provider.Started.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, provider.Calls);
        provider.Release();
        await queue.WaitForIdleAsync().WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(["a.txt", "b.txt"], provider.Done);
    }

    [Fact]
    public async Task RemovingAWaitingTransferSkipsIt()
    {
        await using var queue = new TransferQueue(new FileOperationService());
        var provider = new HoldingProvider();
        queue.Enqueue(Request(provider, "a.txt"));
        var waiting = queue.Enqueue(Request(provider, "b.txt"));
        await provider.Started.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(queue.Cancel(waiting.Id));
        Assert.Equal(0, queue.WaitingCount);
        provider.Release();
        await queue.WaitForIdleAsync().WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["a.txt"], provider.Done);
    }

    [Fact]
    public async Task CancellingTheRunningTransferContinuesWithTheNext()
    {
        await using var queue = new TransferQueue(new FileOperationService());
        var provider = new HoldingProvider { BlockFirstOnCancellation = true };
        var running = queue.Enqueue(Request(provider, "a.txt"));
        queue.Enqueue(Request(provider, "b.txt"));
        await provider.Started.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(queue.Cancel(running.Id));
        await queue.WaitForIdleAsync().WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["b.txt"], provider.Done);
    }

    private static TransferRequest Request(IFileSystemProvider provider, string name) => new()
    {
        Source = provider, Destination = provider,
        Entries = [new FileEntry(name, name, false, 4, null, ".txt")],
        DestinationDirectory = "dest", Move = false, Label = name,
        ResolveConflict = _ => Task.FromResult(new ConflictResolution(ConflictAction.Cancel))
    };

    private sealed class HoldingProvider : IFileSystemProvider
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public int Calls => _calls;
        public Task Started => _started.Task;
        public bool BlockFirstOnCancellation { get; init; }
        public List<string> Done { get; } = [];
        public void Release() => _release.TrySetResult();
        public string Id => "holding";
        public string DisplayName => "Holding";
        public bool SupportsRecycle => false;
        public StringComparer PathComparer => StringComparer.Ordinal;
        public bool CanHandle(string path) => true;
        public string NormalizePath(string path) => path;
        public string CombinePath(string directory, string name) => directory + "/" + name;
        public bool IsSameOrDescendant(string path, string ancestor) => false;
        public Task<IReadOnlyList<LocationEntry>> GetLocationsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LocationEntry>>([]);
        public Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FileEntry>>([]);
        public Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<FileEntry?>(null);
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string path, DeleteMode mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveEmptyDirectoryAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MoveFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task TransferFileAsync(string sourcePath, string destinationPath, bool overwrite, bool move,
            IProgress<long>? bytesCopied, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                _started.TrySetResult();
                if (BlockFirstOnCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
                else await _release.Task.WaitAsync(cancellationToken);
            }
            lock (Done) Done.Add(sourcePath);
        }
    }
}
