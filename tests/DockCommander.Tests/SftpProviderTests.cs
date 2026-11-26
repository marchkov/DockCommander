using DockCommander.App;
using DockCommander.Core.Connections;
using DockCommander.Core.Files;
using DockCommander.Core.Operations;
using FluentFTP;
using Xunit;

#pragma warning disable xUnit1051

namespace DockCommander.Tests;

public sealed class SftpProviderTests
{
    [Fact]
    public async Task ListsPathsAndCopiesBetweenLocalAndTwoRemotes()
    {
        using var temp = new ScratchDirectory();
        var local = new LocalFileSystemProvider();
        var from = temp.Folder("from");
        var to = temp.Folder("to");
        temp.File("from/a.txt", "hello");
        var source = new MemorySftp();
        source.AddDirectory("/");
        source.AddDirectory("/tree");
        source.AddDirectory("/tree/inside");
        source.AddFile("/tree/a.txt", "keep");
        var other = new MemorySftp();
        other.AddDirectory("/");
        var left = new SftpFileSystemProvider(source, "left", "Left", "/");
        var right = new SftpFileSystemProvider(other, "right", "Right", "/var");
        var service = new FileOperationService();
        static Task<ConflictResolution> Overwrite(FileConflict _) => Task.FromResult(new ConflictResolution(ConflictAction.Overwrite));

        var root = await left.ListAsync("/");
        Assert.DoesNotContain(root, entry => entry.Name == "..");
        var tree = await left.ListAsync("/tree");
        Assert.Equal("..", tree[0].Name);
        Assert.Equal("/", left.DisplayPath(tree[0].FullPath));
        Assert.Contains(tree, entry => entry.Name == "inside" && entry.IsDirectory);
        Assert.Contains(tree, entry => entry.Name == "a.txt" && !entry.IsDirectory);
        Assert.False(left.CanHandle(right.NormalizePath("/same.txt")));
        Assert.Throws<IOException>(() => left.NormalizePath("sftp://right/same.txt"));

        var folder = (await left.GetEntryAsync("/tree"))!;
        await Assert.ThrowsAsync<IOException>(() => service.TransferAsync(left, left, [folder], "/tree/inside", false, Overwrite));
        Assert.Null(await left.GetEntryAsync("/tree/inside/a.txt"));

        var localFile = (await local.GetEntryAsync(Path.Combine(from, "a.txt")))!;
        await service.TransferAsync(local, left, [localFile], "/", false, Overwrite);
        Assert.Equal("hello", source.ReadFile("/a.txt"));
        Assert.True(File.Exists(Path.Combine(from, "a.txt")));

        var remoteFile = (await left.GetEntryAsync("/a.txt"))!;
        await service.TransferAsync(left, right, [remoteFile], "/", true, Overwrite);
        Assert.Equal("hello", other.ReadFile("/a.txt"));
        Assert.Null(await left.GetEntryAsync("/a.txt"));

        var downloaded = (await right.GetEntryAsync("/a.txt"))!;
        await service.TransferAsync(right, local, [downloaded], to, false, Overwrite);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(to, "a.txt")));

        var renamed = await left.RenameAsync("/tree/a.txt", "b.txt");
        Assert.Equal("/tree/b.txt", left.DisplayPath(renamed));
        await left.DeleteAsync("/tree", DeleteMode.Permanent);
        Assert.Null(await left.GetEntryAsync("/tree"));
        await Assert.ThrowsAsync<NotSupportedException>(() => left.DeleteAsync("/tree/b.txt", DeleteMode.Recycle));
        var ftp = new SftpFileSystemProvider(source, "left", "FTP", "/", "ftp");
        Assert.False(ftp.CanHandle(left.NormalizePath("/tree")));
        Assert.False(left.CanHandle(ftp.NormalizePath("/tree")));
        Assert.Equal("/incoming", ftp.DisplayPath(ftp.NormalizePath("incoming")));
    }

    [Fact]
    public async Task CreateFileWritesAnEmptyFileAndRefusesADuplicateName()
    {
        var remote = new MemorySftp();
        remote.AddDirectory("/");
        using var provider = new SftpFileSystemProvider(remote, "box", "Box", "/");
        var service = new FileOperationService();
        var path = await service.CreateFileAsync(provider, "/", "notes.txt");
        Assert.Equal("/notes.txt", provider.DisplayPath(path));
        Assert.Empty(remote.ReadFile("/notes.txt"));
        await Assert.ThrowsAsync<IOException>(() => service.CreateFileAsync(provider, "/", "notes.txt"));
        Assert.Empty(remote.ReadFile("/notes.txt"));
    }

    [Fact]
    public void FtpsUsesExplicitTlsUnlessThePortIs990()
    {
        Assert.Equal(FtpEncryptionMode.None, FluentFtpRemote.Encryption(ConnectionProtocols.Ftp, 21));
        Assert.Equal(FtpEncryptionMode.Explicit, FluentFtpRemote.Encryption(ConnectionProtocols.Ftps, 21));
        Assert.Equal(FtpEncryptionMode.Implicit, FluentFtpRemote.Encryption(ConnectionProtocols.Ftps, 990));
    }

    [Fact]
    public async Task FailedUploadKeepsTheFileAndAFailedRenameLeavesNoTemporaryFile()
    {
        var remote = new MemorySftp();
        remote.AddDirectory("/");
        remote.AddFile("/keep.txt", "old");
        using var provider = new SftpFileSystemProvider(remote, "box", "Box", "/");
        var path = provider.NormalizePath("/keep.txt");
        remote.FailWrite = true;
        await using (var content = new MemoryStream("new"u8.ToArray()))
            await Assert.ThrowsAsync<IOException>(() => provider.WriteFileAsync(path, content, true, null));
        Assert.Equal("old", remote.ReadFile("/keep.txt"));
        Assert.DoesNotContain(await provider.ListAsync("/"), entry => entry.Name.StartsWith(".dc-", StringComparison.Ordinal));

        remote.FailWrite = false;
        remote.FailRename = true;
        await using (var content = new MemoryStream("new"u8.ToArray()))
            await Assert.ThrowsAsync<IOException>(() => provider.WriteFileAsync(path, content, true, null));
        Assert.Null(await provider.GetEntryAsync(path));
        Assert.DoesNotContain(await provider.ListAsync("/"), entry => entry.Name.StartsWith(".dc-", StringComparison.Ordinal));
    }
}

internal sealed class MemorySftp : ISftpRemote
{
    private sealed class Node(bool directory, byte[]? content)
    {
        public bool Directory { get; } = directory;
        public byte[]? Content = content;
    }

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);

    public void AddDirectory(string path) => _nodes[Clean(path)] = new Node(true, null);

    public void AddFile(string path, string text) => _nodes[Clean(path)] = new Node(false, System.Text.Encoding.UTF8.GetBytes(text));

    public string ReadFile(string path) => System.Text.Encoding.UTF8.GetString(_nodes[Clean(path)].Content!);

    public bool FailClosed { get; set; }
    public bool FailWrite { get; set; }
    public bool FailRename { get; set; }
    public bool FailRead { get; set; }

    public IReadOnlyList<SftpRemoteEntry> List(string path, CancellationToken cancellationToken)
    {
        if (FailClosed) throw new IOException("The SFTP connection closed.");
        path = Clean(path);
        if (!_nodes.TryGetValue(path, out var node) || !node.Directory) throw new IOException("The folder is not there.");
        var prefix = path == "/" ? "/" : path + "/";
        return _nodes.Where(pair =>
        {
            if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var rest = pair.Key[prefix.Length..];
            return rest.Length > 0 && !rest.Contains('/');
        }).Select(pair => Entry(pair.Key, pair.Value)).ToArray();
    }

    public SftpRemoteEntry? TryStat(string path, CancellationToken cancellationToken)
    {
        path = Clean(path);
        return _nodes.TryGetValue(path, out var node) ? Entry(path, node) : null;
    }

    public Stream OpenRead(string path, CancellationToken cancellationToken)
    {
        if (FailRead) throw new IOException("The file could not be read.");
        path = Clean(path);
        if (!_nodes.TryGetValue(path, out var node) || node.Directory) throw new IOException("Select a regular file.");
        return new MemoryStream(node.Content ?? [], writable: false);
    }

    public Stream OpenWrite(string path, CancellationToken cancellationToken)
    {
        if (FailWrite) throw new IOException("The upload failed.");
        path = Clean(path);
        var memory = new MemoryStream();
        return new CommitStream(memory, () => _nodes[path] = new Node(false, memory.ToArray()));
    }

    public void CreateDirectory(string path, CancellationToken cancellationToken)
    {
        path = Clean(path);
        if (_nodes.ContainsKey(path)) throw new IOException("An entry with this name already exists.");
        _nodes[path] = new Node(true, null);
    }

    public void DeleteFile(string path, CancellationToken cancellationToken)
    {
        path = Clean(path);
        if (!_nodes.Remove(path)) throw new IOException("The file is no longer there.");
    }

    public void DeleteDirectory(string path, CancellationToken cancellationToken)
    {
        path = Clean(path);
        if (_nodes.Keys.Any(key => key.StartsWith(path + "/", StringComparison.Ordinal)))
            throw new IOException("The folder is not empty.");
        if (!_nodes.Remove(path)) throw new IOException("The folder is no longer there.");
    }

    public void Rename(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        if (FailRename) throw new IOException("The rename failed.");
        var source = Clean(sourcePath);
        var destination = Clean(destinationPath);
        if (!_nodes.ContainsKey(source)) throw new IOException("The item is no longer there.");
        if (_nodes.ContainsKey(destination)) throw new IOException("An entry with this name already exists.");
        var moves = _nodes.Keys.Where(key => key == source || key.StartsWith(source + "/", StringComparison.Ordinal)).ToArray();
        foreach (var key in moves)
        {
            var node = _nodes[key];
            _nodes.Remove(key);
            _nodes[destination + key[source.Length..]] = node;
        }
    }

    public void Dispose() { }

    private static SftpRemoteEntry Entry(string path, Node node)
    {
        var name = path == "/" ? "/" : path[(path.LastIndexOf('/') + 1)..];
        return new(name, path, node.Directory, false, node.Directory ? null : node.Content?.LongLength, DateTimeOffset.UnixEpoch);
    }

    private static string Clean(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }

    private sealed class CommitStream(MemoryStream memory, Action commit) : MemoryStream
    {
        private bool _committed;
        public override void Write(byte[] buffer, int offset, int count) => memory.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => memory.WriteAsync(buffer, cancellationToken);
        public override void Flush() => memory.Flush();
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_committed) { _committed = true; commit(); }
            base.Dispose(disposing);
        }
    }
}
