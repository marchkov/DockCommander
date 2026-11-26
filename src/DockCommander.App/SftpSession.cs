using System.Security.Cryptography;
using DockCommander.Core.Connections;
using DockCommander.Core.Files;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DockCommander.App;

internal sealed record SftpCredentials(string Authentication, string Password, string KeyPath, string Passphrase, bool Remember = false);

internal interface ISftpConnector
{
    Task<IFileSystemProvider> ConnectAsync(ConnectionProfile profile, SftpCredentials credentials,
        Func<string, bool> trustHost, CancellationToken cancellationToken);
}

internal sealed class SshNetSftpConnector : ISftpConnector
{
    public Task<IFileSystemProvider> ConnectAsync(ConnectionProfile profile, SftpCredentials credentials,
        Func<string, bool> trustHost, CancellationToken cancellationToken) => Task.Run<IFileSystemProvider>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remote = SshNetSftpRemote.Open(profile, credentials, trustHost, cancellationToken);
        return new SftpFileSystemProvider(remote, profile.Id, profile.Name, profile.InitialPath);
    }, cancellationToken);
}

internal sealed class SshNetSftpRemote : ISftpRemote
{
    private readonly SftpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    private SshNetSftpRemote(SftpClient client) => _client = client;

    public static SshNetSftpRemote Open(ConnectionProfile profile, SftpCredentials credentials,
        Func<string, bool> trustHost, CancellationToken cancellationToken)
    {
        var port = profile.Port ?? 22;
        var method = Authentication(profile, credentials);
        var info = new ConnectionInfo(profile.Host, port, profile.Username, method)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        var client = new SftpClient(info) { OperationTimeout = TimeSpan.FromSeconds(30) };
        client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = e.HostKey is { Length: > 0 }
                ? "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey))
                : Convert.ToHexString(e.FingerPrint);
            e.CanTrust = trustHost($"{e.HostKeyName} {fingerprint}");
        };
        try
        {
            using var cancel = cancellationToken.Register(() => { try { client.Dispose(); } catch (Exception) { } });
            cancellationToken.ThrowIfCancellationRequested();
            client.Connect();
            if (!client.IsConnected) throw new IOException("The SFTP server did not accept the connection.");
            return new SshNetSftpRemote(client);
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
        Guard(cancellationToken, () => _client.ListDirectory(path)
            .Select(ToEntry)
            .ToArray());

    public SftpRemoteEntry? TryStat(string path, CancellationToken cancellationToken)
    {
        _gate.Wait(cancellationToken);
        try
        {
            EnsureConnected();
            if (!_client.Exists(path)) return null;
            return ToEntry(_client.Get(path));
        }
        catch (SftpPathNotFoundException) { return null; }
        catch (Exception ex) when (ex is not IOException and not OperationCanceledException)
        {
            throw new IOException(ex.Message, ex);
        }
        finally { _gate.Release(); }
    }

    public Stream OpenRead(string path, CancellationToken cancellationToken) =>
        new LockedStream(Guard(cancellationToken, () => _client.OpenRead(path)), _gate);

    public Stream OpenWrite(string path, CancellationToken cancellationToken) =>
        new LockedStream(Guard(cancellationToken, () => _client.OpenWrite(path)), _gate);

    public void CreateDirectory(string path, CancellationToken cancellationToken) =>
        Guard(cancellationToken, () => _client.CreateDirectory(path));

    public void DeleteFile(string path, CancellationToken cancellationToken) =>
        Guard(cancellationToken, () => _client.DeleteFile(path));

    public void DeleteDirectory(string path, CancellationToken cancellationToken) =>
        Guard(cancellationToken, () => _client.DeleteDirectory(path));

    public void Rename(string sourcePath, string destinationPath, CancellationToken cancellationToken) =>
        Guard(cancellationToken, () => _client.RenameFile(sourcePath, destinationPath));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _gate.Wait();
        try { _client.Dispose(); }
        finally { _gate.Release(); }
        _gate.Dispose();
    }

    private T Guard<T>(CancellationToken cancellationToken, Func<T> action)
    {
        _gate.Wait(cancellationToken);
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
        if (_disposed != 0 || !_client.IsConnected) throw new IOException("The SFTP connection closed.");
    }

    private static AuthenticationMethod Authentication(ConnectionProfile profile, SftpCredentials credentials)
    {
        if (credentials.Authentication == AuthenticationKinds.Password)
            return new PasswordAuthenticationMethod(profile.Username, credentials.Password);
        if (credentials.Authentication != AuthenticationKinds.PrivateKey)
            throw new IOException("SFTP does not support anonymous authentication.");
        if (string.IsNullOrWhiteSpace(credentials.KeyPath) || !File.Exists(credentials.KeyPath))
            throw new IOException("Choose an existing private key file.");
        try
        {
            var key = string.IsNullOrEmpty(credentials.Passphrase)
                ? new PrivateKeyFile(credentials.KeyPath)
                : new PrivateKeyFile(credentials.KeyPath, credentials.Passphrase);
            return new PrivateKeyAuthenticationMethod(profile.Username, key);
        }
        catch (Exception ex) when (ex is not IOException)
        {
            throw new IOException("Could not read the private key.", ex);
        }
    }

    private static SftpRemoteEntry ToEntry(Renci.SshNet.Sftp.ISftpFile file) =>
        new(file.Name, file.FullName, file.IsDirectory, file.IsSymbolicLink,
            file.IsDirectory ? null : file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));

    private sealed class LockedStream(Stream inner, SemaphoreSlim gate) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { gate.Wait(); try { inner.Flush(); } finally { gate.Release(); } }
        public override int Read(byte[] buffer, int offset, int count)
        {
            gate.Wait(); try { return inner.Read(buffer, offset, count); } finally { gate.Release(); }
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            gate.Wait(); try { inner.Write(buffer, offset, count); } finally { gate.Release(); }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);
            try { return await inner.ReadAsync(buffer, cancellationToken); }
            finally { gate.Release(); }
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); }
    }
}
