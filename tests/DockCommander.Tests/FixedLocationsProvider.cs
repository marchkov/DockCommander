using DockCommander.Core.Files;

namespace DockCommander.Tests;

// Keep real filesystem operations but never enumerate the host's removable/network drives.
internal sealed class FixedLocationsProvider(IFileSystemProvider inner,
    Func<CancellationToken, Task<IReadOnlyList<LocationEntry>>> locations) : IFileSystemProvider
{
    public string Id => inner.Id;
    public string DisplayName => inner.DisplayName;
    public bool SupportsRecycle => inner.SupportsRecycle;
    public bool CanWriteFileContent => inner.CanWriteFileContent;
    public bool SupportsLocalFileDrop => inner.SupportsLocalFileDrop;
    public StringComparer PathComparer => inner.PathComparer;
    public bool CanHandle(string path) => inner.CanHandle(path);
    public string NormalizePath(string path) => inner.NormalizePath(path);
    public string CombinePath(string directory, string name) => inner.CombinePath(directory, name);
    public bool IsSameOrDescendant(string path, string ancestor) => inner.IsSameOrDescendant(path, ancestor);
    public Task<IReadOnlyList<LocationEntry>> GetLocationsAsync(CancellationToken cancellationToken = default) => locations(cancellationToken);
    public Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken = default) => inner.ListAsync(path, cancellationToken);
    public Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default) => inner.GetEntryAsync(path, cancellationToken);
    public Task<FileEntry> ResolveDroppedFileAsync(string path, CancellationToken cancellationToken = default) => inner.ResolveDroppedFileAsync(path, cancellationToken);
    public Task ValidateDeleteAsync(string path, DeleteMode mode, CancellationToken cancellationToken = default) => inner.ValidateDeleteAsync(path, mode, cancellationToken);
    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => inner.CreateDirectoryAsync(path, cancellationToken);
    public Task DeleteAsync(string path, DeleteMode mode, CancellationToken cancellationToken = default) => inner.DeleteAsync(path, mode, cancellationToken);
    public Task RemoveEmptyDirectoryAsync(string path, CancellationToken cancellationToken = default) => inner.RemoveEmptyDirectoryAsync(path, cancellationToken);
    public Task CopyFileAsync(string source, string target, bool overwrite, CancellationToken cancellationToken = default) => inner.CopyFileAsync(source, target, overwrite, cancellationToken);
    public Task MoveFileAsync(string source, string target, bool overwrite, CancellationToken cancellationToken = default) => inner.MoveFileAsync(source, target, overwrite, cancellationToken);
    public Task<bool> TryMoveDirectoryAsync(string source, string target, CancellationToken cancellationToken = default) => inner.TryMoveDirectoryAsync(source, target, cancellationToken);
    public Task TransferFileAsync(string source, string target, bool overwrite, bool move, IProgress<long>? progress, CancellationToken cancellationToken = default) => inner.TransferFileAsync(source, target, overwrite, move, progress, cancellationToken);
    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default) => inner.OpenReadAsync(path, cancellationToken);
    public Task WriteFileAsync(string destinationPath, Stream content, bool overwrite, IProgress<long>? bytesCopied, CancellationToken cancellationToken = default) => inner.WriteFileAsync(destinationPath, content, overwrite, bytesCopied, cancellationToken);
    public Task<string?> GetLocalFilePathAsync(string path, CancellationToken cancellationToken = default) => inner.GetLocalFilePathAsync(path, cancellationToken);
    public Task<string> RenameAsync(string path, string name, CancellationToken cancellationToken = default) => inner.RenameAsync(path, name, cancellationToken);
}
