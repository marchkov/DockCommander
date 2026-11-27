namespace DockCommander.Core.Files;

public enum DeleteMode { Recycle, Permanent }

public interface IFileSystemProvider
{
    string Id { get; }
    string DisplayName { get; }
    bool SupportsRecycle { get; }
    bool CanWriteFileContent => false;
    bool SupportsLocalFileDrop => false;
    Task<FileEntry> ResolveDroppedFileAsync(string localPath, CancellationToken cancellationToken = default) =>
        Task.FromException<FileEntry>(new NotSupportedException("This provider cannot accept files from other applications."));
    StringComparer PathComparer { get; }
    // Name and path checks that belong to one directory. The default is the provider-wide comparer.
    StringComparer ComparerFor(string path) => PathComparer;
    bool CanHandle(string path);
    string NormalizePath(string path);
    string DisplayPath(string path) => path;
    string CombinePath(string directory, string name);
    bool IsSameOrDescendant(string path, string ancestor);
    Task<IReadOnlyList<LocationEntry>> GetLocationsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken = default);
    Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default);
    // Called for every selected entry before the first removal. The default accepts the path.
    Task ValidateDeleteAsync(string path, DeleteMode mode, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task DeleteAsync(string path, DeleteMode mode, CancellationToken cancellationToken = default);
    Task RemoveEmptyDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default);
    Task MoveFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default);
    // Renames a real directory onto a path that does not exist yet. False means the caller should copy.
    // Links inside the directory stay links; the default cannot do this and reports false.
    Task<bool> TryMoveDirectoryAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
    Task TransferFileAsync(string sourcePath, string destinationPath, bool overwrite, bool move,
        IProgress<long>? bytesCopied, CancellationToken cancellationToken = default) => move
        ? MoveFileAsync(sourcePath, destinationPath, overwrite, cancellationToken)
        : CopyFileAsync(sourcePath, destinationPath, overwrite, cancellationToken);
    // Optional capabilities: providers without local files can still support the text viewer.
    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromException<Stream>(new NotSupportedException("This provider does not support reading file content."));
    // Writes a new file from a caller-owned stream. The stream is not disposed here.
    Task WriteFileAsync(string destinationPath, Stream content, bool overwrite, IProgress<long>? bytesCopied,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("This provider does not support writing file content."));
    Task<string?> GetLocalFilePathAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    Task<string> RenameAsync(string path, string newName, CancellationToken cancellationToken = default) =>
        Task.FromException<string>(new NotSupportedException("This provider does not support renaming entries."));
}
