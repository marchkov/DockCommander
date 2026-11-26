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
        if (!File.Exists(_path)) return new(StringComparer.Ordinal);
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(_path));
        return entries ?? new(StringComparer.Ordinal);
    }

    private async Task WriteAsync(Dictionary<string, string> entries)
    {
        if (entries.Count == 0)
        {
            if (File.Exists(_path)) File.Delete(_path);
            return;
        }
        Directory.CreateDirectory(KeyDirectory());
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entries));
        SecretProtector.TryRestrict(temporary);
        File.Move(temporary, _path, true);
        SecretProtector.TryRestrict(_path);
    }
}

internal static class SecretProtector
{
    public static string Protect(string text, string keyDirectory)
    {
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

    public static void TryRestrict(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static byte[] Key(string keyDirectory)
    {
        Directory.CreateDirectory(keyDirectory);
        var keyPath = System.IO.Path.Combine(keyDirectory, "protector.key");
        if (!File.Exists(keyPath))
        {
            File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
            TryRestrict(keyPath);
        }
        var key = File.ReadAllBytes(keyPath);
        if (key.Length != 32) throw new IOException("The secret key file is unusable.");
        return key;
    }
}
