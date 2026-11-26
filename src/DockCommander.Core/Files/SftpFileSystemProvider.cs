namespace DockCommander.Core.Files;

public sealed class SftpFileSystemProvider : IFileSystemProvider, IDisposable
{
    private readonly ISftpRemote _remote;
    private readonly string _id;
    private readonly string _displayName;
    private readonly string _initial;
    private readonly string _scheme;
    private readonly string _prefix;

    public SftpFileSystemProvider(ISftpRemote remote, string connectionId, string displayName, string initialPath, string scheme = "sftp")
    {
        if (string.IsNullOrWhiteSpace(connectionId) || connectionId.Contains('/') || connectionId.Contains(':'))
            throw new ArgumentException("A connection id cannot contain ':' or '/'.");
        if (scheme is not ("sftp" or "ftp" or "webdav")) throw new ArgumentException("The remote scheme must be sftp, ftp, or webdav.");
        _remote = remote;
        _id = connectionId.Trim();
        _scheme = scheme;
        _displayName = string.IsNullOrWhiteSpace(displayName) ? _id : displayName.Trim();
        _prefix = scheme + "://" + _id;
        _initial = NormalizePath(string.IsNullOrWhiteSpace(initialPath) ? "/" : initialPath);
    }

    public string Id => _scheme + ":" + _id;
    public string DisplayName => _displayName;
    public bool SupportsRecycle => false;
    public bool CanWriteFileContent => true;
    public StringComparer PathComparer => StringComparer.Ordinal;

    public bool CanHandle(string path) =>
        !string.IsNullOrWhiteSpace(path) && path.Trim().StartsWith(_prefix + "/", StringComparison.Ordinal);

    public string DisplayPath(string path) => Unix(path);

    public string NormalizePath(string path) =>
        TryNormalize(path, out var canonical) ? canonical : throw new IOException("Enter an absolute remote path.");

    private bool TryNormalize(string path, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        var text = path.Trim().Replace('\\', '/');
        string unix;
        if (text.StartsWith(_prefix, StringComparison.Ordinal)) unix = text[_prefix.Length..];
        else if (text.StartsWith('/')) unix = text;
        else if (!text.Contains(':')) unix = "/" + text;
        else return false;
        var parts = new List<string>();
        foreach (var part in unix.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        canonical = _prefix + "/" + string.Join('/', parts);
        return true;
    }

    public string CombinePath(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains('/') || name.Contains('\\') || name.Any(char.IsControl))
            throw new ArgumentException("Enter a single valid file or folder name.");
        var parent = NormalizePath(directory);
        return NormalizePath(parent + "/" + name);
    }

    public bool IsSameOrDescendant(string path, string ancestor)
    {
        if (!TryNormalize(path, out path) || !TryNormalize(ancestor, out ancestor)) return false;
        if (PathComparer.Equals(path, ancestor)) return true;
        var prefix = ancestor.EndsWith('/') ? ancestor : ancestor + "/";
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }

    public Task<IReadOnlyList<LocationEntry>> GetLocationsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LocationEntry>>([new(_id, _displayName, DisplayPath(_initial), "Remote")]);

    public Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<FileEntry>>(() =>
    {
        var canonical = NormalizePath(path);
        var unix = Unix(canonical);
        var listed = _remote.List(unix, cancellationToken);
        var items = new List<FileEntry>();
        if (unix != "/")
        {
            var slash = unix.LastIndexOf('/');
            var parent = slash <= 0 ? "/" : unix[..slash];
            items.Add(new("..", Canonical(parent), true, null, null));
        }
        items.AddRange(listed
            .Where(entry => entry.Name is not ("." or ".."))
            .Select(ToFile)
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase));
        return items;
    }, cancellationToken);

    public Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var unix = Unix(path);
        if (unix == "/") return new FileEntry("/", Canonical("/"), true, null, null);
        return _remote.TryStat(unix, cancellationToken) is { } entry ? ToFile(entry) : null;
    }, cancellationToken);

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var unix = Unix(path);
        if (unix == "/") return;
        var existing = _remote.TryStat(unix, cancellationToken);
        if (existing is { IsDirectory: true, IsLink: false }) return;
        if (existing is not null) throw new IOException("A file with this name already exists.");
        _remote.CreateDirectory(unix, cancellationToken);
    }, cancellationToken);

    public Task ValidateDeleteAsync(string path, DeleteMode mode, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (mode == DeleteMode.Recycle) throw new NotSupportedException("The recycle bin is unavailable. Nothing was deleted. Shift+F8 explicitly deletes permanently.");
        if (Unix(path) == "/") throw new IOException("The remote root cannot be deleted.");
    }, cancellationToken);

    public Task DeleteAsync(string path, DeleteMode mode, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (mode == DeleteMode.Recycle) throw new NotSupportedException("The recycle bin is unavailable. Nothing was deleted. Shift+F8 explicitly deletes permanently.");
        DeleteTree(Unix(path), cancellationToken);
    }, cancellationToken);

    public Task RemoveEmptyDirectoryAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var unix = Unix(path);
        if (unix == "/") throw new IOException("The remote root cannot be deleted.");
        var children = _remote.List(unix, cancellationToken).Where(entry => entry.Name is not ("." or "..")).ToArray();
        if (children.Length > 0) throw new IOException("The folder is not empty.");
        _remote.DeleteDirectory(unix, cancellationToken);
    }, cancellationToken);

    public Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default) =>
        TransferFileAsync(sourcePath, destinationPath, overwrite, false, null, cancellationToken);

    public Task MoveFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default) =>
        TransferFileAsync(sourcePath, destinationPath, overwrite, true, null, cancellationToken);

    public Task<bool> TryMoveDirectoryAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var source = Unix(sourcePath);
        var destination = Unix(destinationPath);
        if (source == "/") return false;
        _remote.Rename(source, destination, cancellationToken);
        return true;
    }, cancellationToken);

    public Task TransferFileAsync(string sourcePath, string destinationPath, bool overwrite, bool move,
        IProgress<long>? bytesCopied, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var source = Unix(sourcePath);
        var destination = Unix(destinationPath);
        var info = _remote.TryStat(source, cancellationToken) ?? throw new FileNotFoundException("The file is no longer there.", source);
        if (info.IsDirectory || info.IsLink) throw new IOException("Select a regular file.");
        if (move && !overwrite && _remote.TryStat(destination, cancellationToken) is null)
        {
            _remote.Rename(source, destination, cancellationToken);
            bytesCopied?.Report(info.Size ?? 0);
            return;
        }
        using var input = _remote.OpenRead(source, cancellationToken);
        WriteUnix(destination, input, overwrite, bytesCopied, cancellationToken);
        if (move) _remote.DeleteFile(source, cancellationToken);
    }, cancellationToken);

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var info = _remote.TryStat(Unix(path), cancellationToken) ?? throw new FileNotFoundException("The file is no longer there.");
        if (info.IsDirectory) throw new IOException("Select a regular file.");
        return _remote.OpenRead(info.Path, cancellationToken);
    }, cancellationToken);

    public Task WriteFileAsync(string destinationPath, Stream content, bool overwrite, IProgress<long>? bytesCopied,
        CancellationToken cancellationToken = default) => Task.Run(() =>
        WriteUnix(Unix(destinationPath), content, overwrite, bytesCopied, cancellationToken), cancellationToken);

    public Task<string> RenameAsync(string path, string newName, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var source = Unix(path);
        if (source == "/") throw new IOException("The remote root cannot be renamed.");
        var destination = Unix(CombinePath(Canonical(Parent(source)), newName));
        if (source == destination) return Canonical(destination);
        if (_remote.TryStat(destination, cancellationToken) is not null)
            throw new IOException("An entry with this name already exists. Choose another name.");
        _remote.Rename(source, destination, cancellationToken);
        return Canonical(destination);
    }, cancellationToken);

    public void Dispose() => _remote.Dispose();

    private void WriteUnix(string destination, Stream content, bool overwrite, IProgress<long>? bytesCopied, CancellationToken cancellationToken)
    {
        if (destination == "/") throw new IOException("A file needs a name.");
        var temp = Parent(destination) is "/" ? "/.dc-" + Guid.NewGuid().ToString("N") + ".tmp"
            : Parent(destination) + "/.dc-" + Guid.NewGuid().ToString("N") + ".tmp";
        var committed = false;
        try
        {
            using (var target = _remote.OpenWrite(temp, cancellationToken))
            {
                var buffer = new byte[131072];
                long copied = 0;
                int read;
                while ((read = content.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    target.Write(buffer, 0, read);
                    copied += read;
                    bytesCopied?.Report(copied);
                }
                target.Flush();
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (_remote.TryStat(destination, cancellationToken) is { } existing)
            {
                if (!overwrite) throw new IOException("An entry with this name already exists.");
                if (existing.IsDirectory && !existing.IsLink) throw new IOException("A file cannot replace a folder.");
                _remote.DeleteFile(destination, cancellationToken);
            }
            _remote.Rename(temp, destination, cancellationToken);
            committed = true;
        }
        finally
        {
            if (!committed && _remote.TryStat(temp, CancellationToken.None) is not null)
            {
                try { _remote.DeleteFile(temp, CancellationToken.None); } catch (IOException) { }
            }
        }
    }

    private void DeleteTree(string unix, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (unix == "/") throw new IOException("The remote root cannot be deleted.");
        var info = _remote.TryStat(unix, cancellationToken) ?? throw new FileNotFoundException("The item is no longer there.", unix);
        if (info.IsLink || !info.IsDirectory)
        {
            _remote.DeleteFile(unix, cancellationToken);
            return;
        }
        foreach (var child in _remote.List(unix, cancellationToken).Where(entry => entry.Name is not ("." or "..")))
            DeleteTree(child.Path, cancellationToken);
        _remote.DeleteDirectory(unix, cancellationToken);
    }

    private FileEntry ToFile(SftpRemoteEntry entry)
    {
        var directory = entry.IsDirectory && !entry.IsLink;
        return new(entry.Name, Canonical(entry.Path), directory, directory ? null : entry.Size, entry.Modified,
            directory ? null : System.IO.Path.GetExtension(entry.Name), entry.IsLink);
    }

    private string Unix(string path)
    {
        var canonical = NormalizePath(path);
        var unix = canonical[_prefix.Length..];
        return unix.Length == 0 ? "/" : unix;
    }

    private string Canonical(string unix) => _prefix + (unix.StartsWith('/') ? unix : "/" + unix);

    private static string Parent(string unix)
    {
        if (unix == "/") return "/";
        var slash = unix.LastIndexOf('/');
        return slash <= 0 ? "/" : unix[..slash];
    }
}
