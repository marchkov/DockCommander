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
        Assert.Equal(443, ConnectionProtocols.DefaultPort(ConnectionProtocols.WebDav));
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

    [Fact]
    public void WebDavPresetsBuildTheEndpointAndNormalizeSlashes()
    {
        var root = Preset(WebDavPresets.Nextcloud, "https://cloud.example.com", "denis");
        var slashed = Preset(WebDavPresets.Nextcloud, "https://cloud.example.com/", "denis");
        Assert.Equal("https://cloud.example.com/remote.php/dav/files/denis/", root.EffectiveUrl);
        Assert.Equal(root.EffectiveUrl, slashed.EffectiveUrl);
        Assert.Equal("/remote.php/dav/files/denis", root.RootPath);
        Assert.Equal("cloud.example.com", root.Host);
        Assert.Null(root.StoredPort);

        var nested = Preset(WebDavPresets.Nextcloud, "https://example.com/nextcloud", "denis");
        var nestedSlash = Preset(WebDavPresets.Nextcloud, "https://example.com/nextcloud/", "denis");
        Assert.Equal("https://example.com/nextcloud/remote.php/dav/files/denis/", nested.EffectiveUrl);
        Assert.Equal(nested.EffectiveUrl, nestedSlash.EffectiveUrl);

        var named = Preset(WebDavPresets.Nextcloud, "https://cloud.example.com/", "Ada Lovelace");
        Assert.Equal("https://cloud.example.com/remote.php/dav/files/Ada%20Lovelace/", named.EffectiveUrl);
        Assert.Equal("/remote.php/dav/files/Ada Lovelace", named.RootPath);

        var own = Preset(WebDavPresets.OwnCloud, "https://example.com/owncloud/", "denis");
        Assert.Equal("https://example.com/owncloud/remote.php/dav/files/denis/", own.EffectiveUrl);
        var classic = WebDavLocations.Resolve(Profile(WebDavPresets.OwnCloud, "https://example.com/owncloud/", "denis"), ownCloudClassic: true);
        Assert.Equal("https://example.com/owncloud/remote.php/webdav/", classic.EffectiveUrl);

        var generic = Preset(WebDavPresets.Generic, "https://dav.example.com/files/", "denis");
        Assert.Equal("https://dav.example.com/files/", generic.EffectiveUrl);
        Assert.Equal("/files", generic.RootPath);
        Assert.Equal("https://dav.example.com/files", Preset(WebDavPresets.Generic, "https://dav.example.com/files", "denis").EffectiveUrl);

        var synology = Preset(WebDavPresets.Synology, "https://nas.example.com:5006/", "denis");
        Assert.Equal("https://nas.example.com:5006/", synology.EffectiveUrl);
        Assert.Equal("/", synology.RootPath);
        Assert.Equal(5006, synology.StoredPort);

        var qnap = Preset(WebDavPresets.Qnap, "https://nas.example.com/Public", "denis");
        Assert.Equal("https://nas.example.com/Public", qnap.EffectiveUrl);
        Assert.Equal("/Public", qnap.RootPath);

        Assert.Equal("Enter a user name.", ConnectionProfile.Validate(Profile(WebDavPresets.Nextcloud, "https://cloud.example.com/", "")));
        Assert.Equal("Enter an http or https server URL.", ConnectionProfile.Validate(Profile(WebDavPresets.Generic, "ftp://files.example.com/", "denis")));
        Assert.Equal("WebDAV does not support private key authentication.",
            ConnectionProfile.Validate(Profile(WebDavPresets.Generic, "https://dav.example.com/files/", "denis") with { Authentication = AuthenticationKinds.PrivateKey }));
        Assert.Equal("WebDAV · Nextcloud · cloud.example.com", rootProfile().Detail);

        ConnectionProfile rootProfile() => Profile(WebDavPresets.Nextcloud, "https://cloud.example.com/", "denis") with { Host = "cloud.example.com" };
    }

    [Fact]
    public async Task WebDavProfileWithoutAPresetStillLoads()
    {
        using var temp = new ScratchDirectory();
        var path = Path.Combine(temp.Root, "connections.json");
        await File.WriteAllTextAsync(path, """
            [{"Id":"cloud","Name":"Cloud","Protocol":"WebDAV","Host":"drive.example.com","Port":443,"Username":"denis","InitialPath":"/remote.php/dav/files/denis","Authentication":"Password"}]
            """, TestContext.Current.CancellationToken);
        var loaded = Assert.Single(await new ConnectionStore(path).LoadAsync());
        Assert.Null(loaded.WebDavPreset);
        Assert.Null(loaded.ServerUrl);
        Assert.Equal("/remote.php/dav/files/denis", loaded.InitialPath);
        Assert.Null(ConnectionProfile.Validate(loaded));
        await new ConnectionStore(path).SaveAsync([loaded]);
        var saved = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("WebDavPreset", saved);
        Assert.DoesNotContain("ServerUrl", saved);
        using var document = JsonDocument.Parse(saved);
        Assert.DoesNotContain(document.RootElement[0].EnumerateObject().Select(property => property.Name), name => name is "Password" or "Passphrase");
    }

    private static ConnectionProfile Profile(string preset, string url, string username) =>
        new("cloud", "Cloud", ConnectionProtocols.WebDav, "pending", Username: username, WebDavPreset: preset, ServerUrl: url);

    private static WebDavLocation Preset(string preset, string url, string username) =>
        WebDavLocations.Resolve(Profile(preset, url, username));
}
