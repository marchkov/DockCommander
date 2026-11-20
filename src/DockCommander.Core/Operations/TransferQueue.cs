using System.Threading.Channels;
using DockCommander.Core.Files;

namespace DockCommander.Core.Operations;

public enum TransferQueueState { Waiting, Running }

public sealed record QueuedTransfer(Guid Id, string Label, bool Move, string DestinationDirectory, TransferQueueState State);

public sealed class TransferRequest
{
    public required IFileSystemProvider Source { get; init; }
    public required IFileSystemProvider Destination { get; init; }
    public required IReadOnlyList<FileEntry> Entries { get; init; }
    public required string DestinationDirectory { get; init; }
    public required bool Move { get; init; }
    public required string Label { get; init; }
    public required Func<FileConflict, Task<ConflictResolution>> ResolveConflict { get; init; }
    public Func<FileConflict, CancellationToken, Task<ConflictResolution>>? ResolveConflictWithCancellation { get; init; }
}

public sealed class TransferQueue : IAsyncDisposable
{
    private readonly FileOperationService _operations;
    private readonly Channel<Work> _incoming = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _sync = new();
    private readonly List<Work> _waiting = [];
    private Work? _running;
    private int _outstanding;
    private TaskCompletionSource _idle = CompletedIdle();
    private readonly Task _worker;
    private bool _disposed;

    public TransferQueue(FileOperationService operations)
    {
        _operations = operations;
        _worker = Task.Run(ProcessAsync);
    }

    public event Action? Changed;
    public Func<string, Task>? JobCompleted { get; set; }
    public TransferProgress? FileProgress { get; private set; }
    public BatchTransferProgress? BatchProgress { get; private set; }

    public bool IsIdle
    {
        get { lock (_sync) return _running is null && _waiting.Count == 0; }
    }

    public int WaitingCount
    {
        get { lock (_sync) return _waiting.Count; }
    }

    public bool IsCancelRequested
    {
        get { lock (_sync) return _running?.IsCanceled == true; }
    }

    public IReadOnlyList<QueuedTransfer> Snapshot()
    {
        lock (_sync)
        {
            var items = new List<QueuedTransfer>(_waiting.Count + (_running is null ? 0 : 1));
            if (_running is { } running) items.Add(running.Describe(TransferQueueState.Running));
            foreach (var work in _waiting) items.Add(work.Describe(TransferQueueState.Waiting));
            return items;
        }
    }

    public QueuedTransfer Enqueue(TransferRequest request)
    {
        Work work;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            work = new Work(request);
            _waiting.Add(work);
            if (_outstanding == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _outstanding++;
            if (!_incoming.Writer.TryWrite(work))
            {
                _waiting.Remove(work); FinishLocked(); work.Cancellation.Dispose();
                throw new ObjectDisposedException(nameof(TransferQueue));
            }
        }
        Changed?.Invoke();
        return work.Describe(TransferQueueState.Waiting);
    }

    public bool Cancel(Guid id)
    {
        lock (_sync)
        {
            if (_running?.Id == id)
            {
                _running.Cancel();
                return true;
            }
            var work = _waiting.FirstOrDefault(item => item.Id == id);
            if (work is null) return false;
            work.Cancel();
            _waiting.Remove(work);
        }
        Changed?.Invoke();
        return true;
    }

    public void CancelRunning()
    {
        lock (_sync) _running?.Cancel();
    }

    public void ClearWaiting()
    {
        lock (_sync)
        {
            foreach (var work in _waiting) work.Cancel();
            _waiting.Clear();
        }
        Changed?.Invoke();
    }

    public Task WaitForIdleAsync()
    {
        lock (_sync) return _idle.Task;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                _disposed = true;
                _incoming.Writer.TryComplete();
                _running?.Cancel();
                foreach (var work in _waiting) work.Cancel();
                _waiting.Clear();
            }
        }
        try { await _worker.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessAsync()
    {
        await foreach (var work in _incoming.Reader.ReadAllAsync())
        {
            lock (_sync)
            {
                _waiting.Remove(work);
                if (work.IsCanceled)
                {
                    work.Cancellation.Dispose();
                    FinishLocked();
                    continue;
                }
                _running = work;
                FileProgress = null;
                BatchProgress = null;
            }
            Changed?.Invoke();
            var message = await RunAsync(work);
            try
            {
                if (JobCompleted is not null) await JobCompleted(message);
            }
            catch
            {
                // The window reports refresh failures. The rest of the queue still runs.
            }
            lock (_sync)
            {
                if (ReferenceEquals(_running, work)) _running = null;
                FileProgress = null;
                BatchProgress = null;
                work.Cancellation.Dispose();
                FinishLocked();
            }
            Changed?.Invoke();
        }
    }

    private async Task<string> RunAsync(Work work)
    {
        try
        {
            var result = await _operations.TransferAsync(work.Request.Source, work.Request.Destination, work.Request.Entries,
                work.Request.DestinationDirectory, work.Request.Move, async conflict =>
                {
                    var token = work.Cancellation.Token;
                    token.ThrowIfCancellationRequested();
                    var resolution = work.Request.ResolveConflictWithCancellation is { } resolve
                        ? resolve(conflict, token) : work.Request.ResolveConflict(conflict);
                    var result = await resolution.WaitAsync(token);
                    token.ThrowIfCancellationRequested();
                    return result;
                },
                progress: null, work.Cancellation.Token,
                new InlineProgress<TransferProgress>(value => FileProgress = value),
                new InlineProgress<BatchTransferProgress>(value => BatchProgress = value));
            return $"{work.Request.Label}: completed {result.Completed}, skipped {result.Skipped}.";
        }
        catch (OperationCanceledException ex)
        {
            return ex.Message.Contains("Already deleted:", StringComparison.Ordinal)
                ? ex.Message
                : $"{work.Request.Label}: cancelled. Already completed changes are kept.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message} Already completed changes are kept.";
        }
    }

    private void FinishLocked()
    {
        _outstanding--;
        if (_outstanding == 0 && _running is null && _waiting.Count == 0) _idle.TrySetResult();
    }

    private static TaskCompletionSource CompletedIdle()
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        idle.TrySetResult();
        return idle;
    }

    private sealed class Work(TransferRequest request)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public TransferRequest Request { get; } = request;
        public CancellationTokenSource Cancellation { get; } = new();
        public bool IsCanceled { get; private set; }
        public void Cancel()
        {
            IsCanceled = true;
            Cancellation.Cancel();
        }
        public QueuedTransfer Describe(TransferQueueState state) =>
            new(Id, Request.Label, Request.Move, Request.DestinationDirectory, state);
    }
}
