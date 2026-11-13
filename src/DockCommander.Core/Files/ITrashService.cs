namespace DockCommander.Core.Files;

public interface ITrashService
{
    bool IsSupported { get; }
    Task RecycleAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class UnsupportedTrashService : ITrashService
{
    public bool IsSupported => false;
    public Task RecycleAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("The recycle bin is unavailable on this platform. Nothing was deleted."));
}
