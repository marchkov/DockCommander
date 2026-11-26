using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using DockCommander.Core.Connections;
using DockCommander.Core.Files;
using FluentFTP;

namespace DockCommander.App;

internal interface IFtpConnector
{
    Task<IFileSystemProvider> ConnectAsync(ConnectionProfile profile, SftpCredentials credentials,
        Func<string, bool, bool> trustCertificate, CancellationToken cancellationToken);
}

internal sealed class FluentFtpConnector : IFtpConnector
{
    public Task<IFileSystemProvider> ConnectAsync(ConnectionProfile profile, SftpCredentials credentials,
        Func<string, bool, bool> trustCertificate, CancellationToken cancellationToken) => Task.Run<IFileSystemProvider>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remote = FluentFtpRemote.Open(profile, credentials, trustCertificate, cancellationToken);
        return new SftpFileSystemProvider(remote, profile.Id, profile.Name, profile.InitialPath, "ftp");
    }, cancellationToken);
}

internal sealed class FluentFtpRemote : ISftpRemote
{
    private readonly FtpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    private FluentFtpRemote(FtpClient client) => _client = client;

    public static FtpEncryptionMode Encryption(string protocol, int port) =>
        protocol != ConnectionProtocols.Ftps ? FtpEncryptionMode.None :
        port == 990 ? FtpEncryptionMode.Implicit : FtpEncryptionMode.Explicit;

    public static FluentFtpRemote Open(ConnectionProfile profile, SftpCredentials credentials,
        Func<string, bool, bool> trustCertificate, CancellationToken cancellationToken)
    {
        var port = profile.Port ?? 21;
        var anonymous = profile.Authentication == AuthenticationKinds.Anonymous;
        var user = anonymous && string.IsNullOrWhiteSpace(profile.Username) ? "anonymous" : profile.Username;
        var password = anonymous ? "anonymous@" : credentials.Password;
        var client = new FtpClient(profile.Host, user, password, port);
        client.Config.EncryptionMode = Encryption(profile.Protocol, port);
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        client.Config.DataConnectionEncryption = client.Config.EncryptionMode != FtpEncryptionMode.None;
        client.Config.ConnectTimeout = 20_000;
        client.Config.ReadTimeout = 30_000;
        client.Config.DataConnectionConnectTimeout = 20_000;
        client.Config.DataConnectionReadTimeout = 30_000;
        client.Config.RetryAttempts = 1;
        client.ValidateCertificate += (_, args) =>
        {
            var certificate = args.Certificate as X509Certificate2;
            var thumb = (certificate?.Thumbprint ?? args.Certificate?.GetCertHashString() ?? "").Replace(" ", "").ToUpperInvariant();
            if (thumb.Length == 0)
            {
                args.Accept = args.PolicyErrors == SslPolicyErrors.None;
                return;
            }
            args.Accept = trustCertificate(thumb, args.PolicyErrors == SslPolicyErrors.None);
        };
        try
        {
            using var cancel = cancellationToken.Register(() => { try { client.Dispose(); } catch (Exception) { } });
            cancellationToken.ThrowIfCancellationRequested();
            client.Connect();
            if (!client.IsConnected) throw new IOException("The FTP server did not accept the connection.");
            return new FluentFtpRemote(client);
        }
        catch (OperationCanceledException) { client.Dispose(); throw; }
        catch (Exception ex) when (ex is not IOException)
        {
            client.Dispose();
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException("The connection was cancelled.", ex, cancellationToken);
            throw new IOException(ex.Message, ex);
        }
    }

    public IReadOnlyList<SftpRemoteEntry> List(string path, CancellationToken cancellationToken) =>
        Guard(cancellationToken, () => Listing(path).Select(item => ToEntry(item, path)).ToArray());

    public SftpRemoteEntry? TryStat(string path, CancellationToken cancellationToken)
    {
        EnterOrBusy(cancellationToken);
        try
        {
            EnsureConnected();
            return _client.GetObjectInfo(path) is { } item ? ToEntry(item, Parent(path)) : null;
        }
        catch (Exception ex) when (ex is not IOException and not OperationCanceledException)
        {
            if (IsMissing(ex)) return null;
            throw new IOException(ex.Message, ex);
        }
        finally { _gate.Release(); }
    }

    public Stream OpenRead(string path, CancellationToken cancellationToken) =>
        Open(cancellationToken, () => _client.OpenRead(path));

    public Stream OpenWrite(string path, CancellationToken cancellationToken) =>
        Open(cancellationToken, () => _client.OpenWrite(path));

    public void CreateDirectory(string path, CancellationToken cancellationToken) =>
        Guard(cancellationToken, () => _client.CreateDirectory(path));

    public void DeleteFile(string path, CancellationToken cancellationToken) =>
        Guard(cancellationToken, () => _client.DeleteFile(path));

    public void DeleteDirectory(string path, CancellationToken cancellationToken) =>
        Guard(cancellationToken, () => _client.DeleteDirectory(path));

    public void Rename(string sourcePath, string destinationPath, CancellationToken cancellationToken) =>
        Guard(cancellationToken, () => _client.Rename(sourcePath, destinationPath));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _gate.Wait();
        try { _client.Dispose(); }
        finally { _gate.Release(); }
        _gate.Dispose();
    }

    private Stream Open(CancellationToken cancellationToken, Func<Stream> open)
    {
        _gate.Wait(cancellationToken);
        try
        {
            EnsureConnected();
            return new HeldStream(open(), _gate);
        }
        catch (Exception ex) when (ex is not IOException and not OperationCanceledException)
        {
            _gate.Release();
            throw new IOException(ex.Message, ex);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private void EnterOrBusy(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_gate.Wait(0)) return;
        cancellationToken.ThrowIfCancellationRequested();
        throw new IOException("This connection is busy transferring a file.");
    }

    private T Guard<T>(CancellationToken cancellationToken, Func<T> action)
    {
        EnterOrBusy(cancellationToken);
        try
        {
            EnsureConnected();
            return action();
        }
        catch (Exception ex) when (ex is not IOException and not OperationCanceledException)
        {
            throw new IOException(ex.Message, ex);
        }
        finally { _gate.Release(); }
    }

    private void Guard(CancellationToken cancellationToken, Action action) =>
        Guard(cancellationToken, () => { action(); return 0; });

    private void EnsureConnected()
    {
        if (_disposed != 0 || !_client.IsConnected) throw new IOException("The FTP connection closed.");
    }

    private static bool IsMissing(Exception exception) =>
        exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("No such file", StringComparison.OrdinalIgnoreCase);

    private FtpListItem[] Listing(string path)
    {
        if (path is not "/" and not "") return _client.GetListing(path);
        try { return _client.GetListing("/"); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return _client.GetListing();
        }
    }

    private static string Parent(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/") return "/";
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    private static SftpRemoteEntry ToEntry(FtpListItem item, string listedDirectory)
    {
        var path = item.FullName?.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
        {
            var parent = string.IsNullOrWhiteSpace(listedDirectory) ? "" : listedDirectory.TrimEnd('/');
            path = parent + "/" + (string.IsNullOrWhiteSpace(item.Name) ? path : item.Name);
        }
        var link = item.Type == FtpObjectType.Link;
        var directory = item.Type == FtpObjectType.Directory;
        DateTimeOffset? modified = item.Modified == DateTime.MinValue ? null : new DateTimeOffset(DateTime.SpecifyKind(item.Modified, DateTimeKind.Utc));
        return new(item.Name, path, directory, link, directory ? null : item.Size, modified);
    }

    private sealed class HeldStream(Stream inner, SemaphoreSlim gate) : Stream
    {
        private int _released;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { inner.Dispose(); }
                finally { if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release(); }
            }
        }
    }
}
