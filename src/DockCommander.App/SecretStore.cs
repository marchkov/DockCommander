using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DockCommander.Core.Connections;

namespace DockCommander.App;

internal sealed record SecretPayload(string Authentication, string Password, string KeyPath, string Passphrase);

internal sealed class SecretStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string Path => _path;

    public SecretStore(string path) => _path = path;

    public static SecretStore Beside(SettingsStore settings) =>
        new(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(settings.FilePath)!, "connections.secrets.json"));

    public async Task<SftpCredentials?> LoadAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            var entries = await ReadAsync();
            if (!entries.TryGetValue(id, out var protectedValue)) return null;
            var json = SecretProtector.Unprotect(protectedValue, KeyDirectory());
            if (string.IsNullOrWhiteSpace(json)) return null;
            var payload = JsonSerializer.Deserialize<SecretPayload>(json);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Authentication)) return null;
            return new SftpCredentials(payload.Authentication, payload.Password ?? "", payload.KeyPath ?? "", payload.Passphrase ?? "", true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            return null;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(string id, SftpCredentials credentials)
    {
        await _gate.WaitAsync();
        try
        {
            var entries = await ReadAsync();
            var payload = new SecretPayload(credentials.Authentication, credentials.Password, credentials.KeyPath, credentials.Passphrase);
            entries[id] = SecretProtector.Protect(JsonSerializer.Serialize(payload), KeyDirectory());
            await WriteAsync(entries);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> ExistsAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            var entries = await ReadAsync();
            return entries.ContainsKey(id);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            var entries = await ReadAsync();
            if (!entries.Remove(id)) return;
            await WriteAsync(entries);
        }
        finally { _gate.Release(); }
    }

    private string KeyDirectory() => System.IO.Path.GetDirectoryName(_path)!;

    private async Task<Dictionary<string, string>> ReadAsync()
    {
        SecretProtector.RejectIfLink(_path);
        if (!File.Exists(_path)) return new(StringComparer.Ordinal);
        SecretProtector.EnsureOwnerOnly(_path, directory: false);
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(_path));
        return entries ?? new(StringComparer.Ordinal);
    }

    private async Task WriteAsync(Dictionary<string, string> entries)
    {
        SecretProtector.RejectIfLink(_path);
        if (entries.Count == 0)
        {
            if (File.Exists(_path)) File.Delete(_path);
            return;
        }
        var directory = KeyDirectory();
        Directory.CreateDirectory(directory);
        SecretProtector.RejectIfLink(directory);
        SecretProtector.EnsureOwnerOnly(directory, directory: true);
        var temporary = _path + ".tmp";
        SecretProtector.RejectIfLink(temporary);
        var json = JsonSerializer.Serialize(entries);
        if (OperatingSystem.IsWindows())
            await File.WriteAllTextAsync(temporary, json);
        else
            SecretProtector.CreateOwnerFile(temporary, System.Text.Encoding.UTF8.GetBytes(json));
        File.Move(temporary, _path, true);
        SecretProtector.EnsureOwnerOnly(_path, directory: false);
    }
}

internal static class SecretProtector
{
    public static string Protect(string text, string keyDirectory)
    {
        RejectIfLink(keyDirectory);
        var plain = Encoding.UTF8.GetBytes(text);
        if (OperatingSystem.IsWindows())
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
        var key = Key(keyDirectory);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, plain, cipher, tag);
        var payload = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, nonce.Length);
        cipher.CopyTo(payload, nonce.Length + tag.Length);
        return "aes1:" + Convert.ToBase64String(payload);
    }

    public static string? Unprotect(string protectedValue, string keyDirectory)
    {
        try
        {
            RejectIfLink(keyDirectory);
            if (protectedValue.StartsWith("dpapi:", StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows()) return null;
                var bytes = Convert.FromBase64String(protectedValue["dpapi:".Length..]);
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
            }
            if (!protectedValue.StartsWith("aes1:", StringComparison.Ordinal)) return null;
            var payload = Convert.FromBase64String(protectedValue["aes1:".Length..]);
            if (payload.Length < 28) return null;
            var nonce = payload.AsSpan(0, 12);
            var tag = payload.AsSpan(12, 16);
            var cipher = payload.AsSpan(28);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(Key(keyDirectory), 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void RejectIfLink(string path)
    {
        try
        {
            if (File.ResolveLinkTarget(path, returnFinalTarget: false) is not null)
                throw new IOException("Credential storage must not follow a symbolic link.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    public static void EnsureOwnerOnly(string path, bool directory)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path) && !Directory.Exists(path)) return;
        var allowed = directory
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(path, allowed);
        var actual = File.GetUnixFileMode(path);
        if ((actual & allowed) != allowed || (actual & ~allowed) != 0)
            throw new IOException("Credential storage could not be restricted to the current user.");
    }

    internal static void CreateOwnerFile(string path, ReadOnlySpan<byte> content)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var stream = new FileStream(path, options))
                stream.Write(content);
            EnsureOwnerOnly(path, directory: false);
        }
        catch
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static byte[] Key(string keyDirectory)
    {
        Directory.CreateDirectory(keyDirectory);
        RejectIfLink(keyDirectory);
        EnsureOwnerOnly(keyDirectory, directory: true);
        var keyPath = System.IO.Path.Combine(keyDirectory, "protector.key");
        RejectIfLink(keyPath);
        if (!File.Exists(keyPath))
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            if (OperatingSystem.IsWindows()) File.WriteAllBytes(keyPath, bytes);
            else CreateOwnerFile(keyPath, bytes);
        }
        else
            EnsureOwnerOnly(keyPath, directory: false);
        var key = File.ReadAllBytes(keyPath);
        if (key.Length != 32) throw new IOException("The secret key file is unusable.");
        return key;
    }
}
