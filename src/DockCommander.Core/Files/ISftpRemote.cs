namespace DockCommander.Core.Files;

public sealed record SftpRemoteEntry(string Name, string Path, bool IsDirectory, bool IsLink, long? Size, DateTimeOffset? Modified);

// Unix paths only. The provider owns the sftp:// connection prefix.
public interface ISftpRemote : IDisposable
{
    IReadOnlyList<SftpRemoteEntry> List(string path, CancellationToken cancellationToken);
    SftpRemoteEntry? TryStat(string path, CancellationToken cancellationToken);
    Stream OpenRead(string path, CancellationToken cancellationToken);
    Stream OpenWrite(string path, CancellationToken cancellationToken);
    void CreateDirectory(string path, CancellationToken cancellationToken);
    void DeleteFile(string path, CancellationToken cancellationToken);
    void DeleteDirectory(string path, CancellationToken cancellationToken);
    void Rename(string sourcePath, string destinationPath, CancellationToken cancellationToken);

    // Replaces destination with the staged file in one operation. False means this protocol
    // cannot replace an existing file by rename, so the caller deletes it first.
    // A thrown exception means the replacement was attempted and the destination is unchanged.
    bool TryReplaceFile(string temporaryPath, string destinationPath, CancellationToken cancellationToken) => false;
}
