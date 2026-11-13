using DockCommander.Core.Operations;

namespace DockCommander.Core.Files;

public sealed class LocalFileSystemProvider(ITrashService? trash = null) : IFileSystemProvider
{
    private readonly ITrashService _trash = trash ?? new UnsupportedTrashService();
    public string Id => "local";
    public string DisplayName => "Local";
    public bool SupportsRecycle => _trash.IsSupported;
    public bool SupportsLocalFileDrop => true;
    public async Task<FileEntry> ResolveDroppedFileAsync(string localPath, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(localPath)) throw new IOException("An absolute local file path is required.");
        var path = NormalizePath(localPath);
        await Task.Run(() => ValidateMutationPath(path), cancellationToken);
        return await GetEntryAsync(path, cancellationToken) ?? throw new FileNotFoundException("The dropped item no longer exists.", path);
    }
    public StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public bool CanHandle(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);
    public string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public string CombinePath(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\') ||
            (OperatingSystem.IsWindows() && (name.EndsWith('.') || name.EndsWith(' '))))
            throw new ArgumentException("Enter a single valid file or folder name.");
        if (OperatingSystem.IsWindows())
        {
            var stem = name.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
                throw new ArgumentException("This name is reserved by Windows.");
        }
        return Path.Combine(NormalizePath(directory), name);
    }

    public bool IsSameOrDescendant(string path, string ancestor)
    {
        path = NormalizePath(path);
        ancestor = NormalizePath(ancestor);
        var prefix = Path.EndsInDirectorySeparator(ancestor) ? ancestor : ancestor + Path.DirectorySeparatorChar;
        return PathComparer.Equals(path, ancestor) || path.StartsWith(prefix,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public Task<IReadOnlyList<LocationEntry>> GetLocationsAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<LocationEntry>>(() =>
    {
        var locations = new List<LocationEntry>();
        var linux = OperatingSystem.IsLinux();
        if (linux)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home)) locations.Add(new(home, "Домой", home, "Home"));
            locations.Add(new("/", "Корень", "/", "Root"));
        }
        foreach (var drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!drive.IsReady) continue;
                var path = drive.RootDirectory.FullName;
                if (linux)
                {
                    if (!LinuxLocations.IsUserMount(path, drive.DriveFormat) || locations.Any(l => PathComparer.Equals(l.RootPath, path))) continue;
                    string? label = null;
                    try { label = drive.VolumeLabel; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    locations.Add(new(path, LinuxLocations.ShortName(path, label), path, drive.DriveType.ToString()));
                }
                else locations.Add(new(drive.Name, drive.Name, path, drive.DriveType.ToString()));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return locations;
    }, cancellationToken);

    public Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<FileEntry>>(() =>
    {
        var directory = new DirectoryInfo(NormalizePath(path));
        var items = new List<FileEntry>();
        if (directory.Parent is not null) items.Add(new("..", directory.Parent.FullName, true, null, null));
        var children = new List<FileEntry>();
        foreach (var info in directory.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            children.Add(ToEntry(info));
        }
        items.AddRange(children.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase));
        return items;
    }, cancellationToken);

    public Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        try
        {
            var attributes = File.GetAttributes(path);
            FileSystemInfo info = attributes.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(path) : new FileInfo(path);
            return ToEntry(info);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }, cancellationToken);

    private static FileEntry ToEntry(FileSystemInfo info)
    {
        var directory = info.Attributes.HasFlag(FileAttributes.Directory);
        return new(info.Name, info.FullName, directory, directory ? null : ((FileInfo)info).Length,
            info.LastWriteTimeUtc, directory ? null : info.Extension, info.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    // Reject linked components for mutations so junctions cannot alias the source
    // or let recursive operations escape the chosen directory.
    public static void ValidateMutationPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (Path.TrimEndingDirectorySeparator(full) == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!))
            throw new IOException("Operations on a filesystem root are not allowed.");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Operations through symbolic links or junctions are not supported yet.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ValidateMutationPath(path);
        Directory.CreateDirectory(path);
    }, cancellationToken);

    public Task ValidateDeleteAsync(string path, DeleteMode mode, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ValidateMutationPath(path);
        if (mode != DeleteMode.Permanent) return;
        var blockers = new List<string>();
        CollectDeleteBlockers(path, blockers, cancellationToken);
        if (blockers.Count > 0) throw new IOException(DeleteFailures.Blocked(blockers));
    }, cancellationToken);

    public async Task DeleteAsync(string path, DeleteMode mode, CancellationToken cancellationToken = default)
    {
        if (mode == DeleteMode.Recycle)
        {
            await Task.Run(() => ValidateMutationPath(path), cancellationToken);
            await _trash.RecycleAsync(path, cancellationToken);
            return;
        }
        var deleted = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                ValidateMutationPath(path);
                var blockers = new List<string>();
                CollectDeleteBlockers(path, blockers, cancellationToken);
                if (blockers.Count > 0) throw new IOException(DeleteFailures.Blocked(blockers));
                DeleteTree(path, cancellationToken, deleted);
            }, cancellationToken);
        }
        catch (Exception ex) when (deleted.Count > 0)
        {
            throw DeleteFailures.WithDeleted(ex, deleted);
        }
    }

    // Links are not followed. Windows folder ReadOnly is an Explorer flag, not a delete lock;
    // only a read-only file is refused, because File.Delete leaves it in place.
    private static void CollectDeleteBlockers(string path, List<string> blockers, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            blockers.Add($"{path} (symbolic link or junction)");
            return;
        }
        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            if (OperatingSystem.IsWindows() && attributes.HasFlag(FileAttributes.ReadOnly))
                blockers.Add($"{path} (read-only)");
            return;
        }
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
            CollectDeleteBlockers(child, blockers, cancellationToken);
    }

    private static void DeleteTree(string path, CancellationToken cancellationToken, List<string> deleted)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateMutationPath(path);
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Operations through symbolic links or junctions are not supported yet.");
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path).OrderBy(child => child, StringComparer.Ordinal))
                DeleteTree(child, cancellationToken, deleted);
            if (OperatingSystem.IsWindows() && attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            Directory.Delete(path, false);
        }
        else File.Delete(path);
        deleted.Add(path);
    }

    public Task RemoveEmptyDirectoryAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ValidateMutationPath(path);
        Directory.Delete(path, false);
    }, cancellationToken);

    public Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default) =>
        TransferFileAsync(sourcePath, destinationPath, overwrite, false, null, cancellationToken);

    public Task TransferFileAsync(string sourcePath, string destinationPath, bool overwrite, bool move,
        IProgress<long>? bytesCopied, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        ValidateTransfer(sourcePath, destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (move && OperatingSystem.IsWindows() &&
            PathComparer.Equals(Path.GetPathRoot(NormalizePath(sourcePath)), Path.GetPathRoot(NormalizePath(destinationPath))))
        {
            File.Move(sourcePath, destinationPath, overwrite);
            return;
        }
        // Stage beside the destination so a failed copy cannot truncate an existing file.
        var temporary = Path.Combine(Path.GetDirectoryName(destinationPath)!, $".dc-{Guid.NewGuid():N}.tmp");
        try
        {
            var modified = File.GetLastWriteTimeUtc(sourcePath);
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[131072];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    copied += read;
                    bytesCopied?.Report(copied);
                }
                await target.FlushAsync(cancellationToken);
            }
            File.SetLastWriteTimeUtc(temporary, modified);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destinationPath, overwrite);
            // Once committed, complete this move before observing cancellation again.
            if (move) File.Delete(sourcePath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }, cancellationToken);

    public Task<bool> TryMoveDirectoryAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var source = NormalizePath(sourcePath);
            var destination = NormalizePath(destinationPath);
            ValidateMutationPath(source);
            ValidateMutationPath(destination);
            if (IsSameOrDescendant(destination, source)) return false;
            var attributes = File.GetAttributes(source);
            // A linked directory is not renamed: moving it can retarget the link instead of the chosen folder.
            if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            if (File.Exists(destination) || Directory.Exists(destination)) return false;
            try
            {
                Directory.Move(source, destination);
                return true;
            }
            catch (IOException) when (Directory.Exists(source) && !Directory.Exists(destination))
            {
                // A different volume refuses the rename and leaves both trees untouched.
                return false;
            }
        }, cancellationToken);

    public Task MoveFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ValidateTransfer(sourcePath, destinationPath);
        File.Move(sourcePath, destinationPath, overwrite);
    }, cancellationToken);

    private void ValidateTransfer(string source, string destination)
    {
        ValidateMutationPath(source);
        ValidateMutationPath(destination);
        if (IsSameOrDescendant(destination, source)) throw new IOException("The destination must not be the source or a folder inside it.");
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default) => Task.Run<Stream>(() =>
    {
        var full = NormalizePath(path);
        if (File.GetAttributes(full).HasFlag(FileAttributes.Directory)) throw new IOException("Select a regular file.");
        return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }, cancellationToken);

    public Task<string?> GetLocalFilePathAsync(string path, CancellationToken cancellationToken = default) => Task.Run<string?>(() =>
    {
        var full = NormalizePath(path);
        if (File.GetAttributes(full).HasFlag(FileAttributes.Directory)) throw new IOException("Select a regular file.");
        return full;
    }, cancellationToken);

    public Task<string> RenameAsync(string path, string newName, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var source = NormalizePath(path);
        ValidateMutationPath(source);
        var destination = CombinePath(Path.GetDirectoryName(source)!, newName);
        ValidateMutationPath(destination);
        var attributes = File.GetAttributes(source);
        if (string.Equals(source, destination, StringComparison.Ordinal)) return destination;
        // Never overwrite or merge. The OS move also checks collisions at execution time.
        if (!PathComparer.Equals(source, destination) && (File.Exists(destination) || Directory.Exists(destination)))
            throw new IOException("An entry with this name already exists. Choose another name.");
        cancellationToken.ThrowIfCancellationRequested();
        if (attributes.HasFlag(FileAttributes.Directory)) Directory.Move(source, destination);
        else File.Move(source, destination, false);
        return destination;
    }, cancellationToken);
}
