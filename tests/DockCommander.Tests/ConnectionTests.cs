using System.Text.Json;
using DockCommander.App;
using DockCommander.Core.Connections;
using Xunit;

namespace DockCommander.Tests;

public sealed class ConnectionTests
{
    [Fact]
    public void DetailShowsThePortOnlyWhenItIsSet()
    {
        var sftp = new ConnectionProfile("a", "Production", "SFTP", "server.example.com", 22);
        var webdav = new ConnectionProfile("b", "Cloud", "WebDAV", "cloud.example.com");
        Assert.Equal("SFTP · server.example.com:22", sftp.Detail);
        Assert.Equal("WebDAV · cloud.example.com", webdav.Detail);
    }

    [Fact]
    public async Task SavedConnectionsDoNotKeepSecrets()
    {
        using var temp = new ScratchDirectory();
        var path = Path.Combine(temp.Root, "connections.json");
        await File.WriteAllTextAsync(path, """
            [{"Id":"prod","Name":"Production","Protocol":"SFTP","Host":"server.example.com","Port":22,"Username":"root","InitialPath":"/var/www","Authentication":"Password","Password":"secret","Passphrase":"hidden"}]
            """, TestContext.Current.CancellationToken);
        var store = new ConnectionStore(path);
        var loaded = await store.LoadAsync();
        var profile = Assert.Single(loaded);
        Assert.Equal("Production", profile.Name);
        Assert.Equal("/var/www", profile.InitialPath);
        await store.SaveAsync(loaded);
        var saved = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("secret", saved);
        Assert.DoesNotContain("hidden", saved);
        using var document = JsonDocument.Parse(saved);
        var names = document.RootElement[0].EnumerateObject().Select(property => property.Name).ToArray();
        Assert.DoesNotContain("Password", names);
        Assert.DoesNotContain("Passphrase", names);
        Assert.Equal("Password", document.RootElement[0].GetProperty("Authentication").GetString());
    }

    [Fact]
    public async Task RememberedSecretRoundTripsWithoutPlaintext()
    {
        using var temp = new ScratchDirectory();
        var store = new SecretStore(Path.Combine(temp.Root, "connections.secrets.json"));
        await store.SaveAsync("prod", new SftpCredentials(AuthenticationKinds.PrivateKey, "", "private-key-path", "key-passphrase-value", true));
        var raw = await File.ReadAllTextAsync(store.Path, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("key-passphrase-value", raw);
        Assert.DoesNotContain("private-key-path", raw);
        Assert.True(raw.Contains("dpapi:", StringComparison.Ordinal) || raw.Contains("aes1:", StringComparison.Ordinal));
        var loaded = await store.LoadAsync("prod");
        Assert.NotNull(loaded);
        Assert.Equal("key-passphrase-value", loaded.Passphrase);
        Assert.Equal("private-key-path", loaded.KeyPath);
        Assert.True(loaded.Remember);
        await store.DeleteAsync("prod");
        Assert.Null(await store.LoadAsync("prod"));
        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public async Task HostFingerprintIsRememberedWithTheConnection()
    {
        using var temp = new ScratchDirectory();
        var profile = new ConnectionProfile("prod", "Production", "SFTP", "server.example.com", 22, "root", "/",
            HostFingerprint: "ssh-ed25519 SHA256:abc");
        Assert.True(profile.Trusts("ssh-ed25519 SHA256:abc"));
        Assert.False(profile.Trusts("ssh-ed25519 SHA256:other"));
        var store = new ConnectionStore(Path.Combine(temp.Root, "connections.json"));
        await store.SaveAsync([profile]);
        var loaded = Assert.Single(await store.LoadAsync());
        Assert.Equal("ssh-ed25519 SHA256:abc", loaded.HostFingerprint);
        Assert.Null((loaded.Normalized() with { HostFingerprint = "  " }).Normalized().HostFingerprint);
    }

    [Fact]
    public void AProfileWithoutAHostIsRejected()
    {
        var profile = new ConnectionProfile("a", "Production", "SFTP", " ");
        Assert.NotNull(ConnectionProfile.Validate(profile));
    }
}
