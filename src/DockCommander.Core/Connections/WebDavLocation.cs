namespace DockCommander.Core.Connections;

public static class WebDavPresets
{
    public const string Nextcloud = "Nextcloud";
    public const string OwnCloud = "ownCloud";
    public const string Synology = "Synology";
    public const string Qnap = "QNAP";
    public const string Generic = "Generic";
    public static readonly IReadOnlyList<string> All = [Nextcloud, OwnCloud, Synology, Qnap, Generic];

    public static bool IsKnown(string? preset) =>
        !string.IsNullOrWhiteSpace(preset) && All.Contains(preset.Trim());

    public static string Example(string? preset) => preset?.Trim() switch
    {
        Nextcloud or OwnCloud => "https://cloud.example.com/",
        Synology => "https://nas.example.com:5006/",
        Qnap => "https://nas.example.com/",
        _ => "https://dav.example.com/files/"
    };
}

public sealed record WebDavLocation(string Scheme, string Host, int Port, string RootPath, string EffectiveUrl)
{
    public int? StoredPort =>
        Scheme == "https" && Port == 443 || Scheme == "http" && Port == 80 ? null : Port;
}

public static class WebDavLocations
{
    public static bool UsesPreset(ConnectionProfile profile) =>
        string.Equals(profile.Protocol.Trim(), ConnectionProtocols.WebDav, StringComparison.OrdinalIgnoreCase) &&
        WebDavPresets.IsKnown(profile.WebDavPreset);

    public static string? Validate(ConnectionProfile profile)
    {
        if (!string.Equals(profile.Protocol.Trim(), ConnectionProtocols.WebDav, StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.IsNullOrWhiteSpace(profile.WebDavPreset) && string.IsNullOrWhiteSpace(profile.ServerUrl))
            return null;
        if (!WebDavPresets.IsKnown(profile.WebDavPreset)) return "Choose a WebDAV type.";
        if (profile.Authentication == AuthenticationKinds.PrivateKey)
            return "WebDAV does not support private key authentication.";
        return TryResolve(profile, ownCloudClassic: false, out _, out var error) ? null : error;
    }

    public static bool TryResolve(ConnectionProfile profile, bool ownCloudClassic, out WebDavLocation location, out string? error)
    {
        location = null!;
        error = null;
        if (Parse(profile.ServerUrl, out var uri, out error) is false) return false;
        var preset = profile.WebDavPreset?.Trim();
        if (preset is WebDavPresets.Nextcloud or WebDavPresets.OwnCloud)
        {
            if (string.IsNullOrWhiteSpace(profile.Username)) { error = "Enter a user name."; return false; }
            if (!IsUserSegment(profile.Username)) { error = "Enter a user name without slashes."; return false; }
        }
        var path = preset switch
        {
            WebDavPresets.Nextcloud => ServicePath(uri, "remote.php/dav/files/" + profile.Username.Trim()),
            WebDavPresets.OwnCloud when ownCloudClassic => ServicePath(uri, "remote.php/webdav"),
            WebDavPresets.OwnCloud => ServicePath(uri, "remote.php/dav/files/" + profile.Username.Trim()),
            _ => NormalizePath(Unescape(uri.AbsolutePath), KeepTrailingSlash(profile.ServerUrl))
        };
        location = new WebDavLocation(uri.Scheme, Host(uri), uri.Port, Root(path), Compose(uri.Scheme, Host(uri), uri.Port, path));
        return true;
    }

    public static WebDavLocation Resolve(ConnectionProfile profile, bool ownCloudClassic = false) =>
        TryResolve(profile, ownCloudClassic, out var location, out var error) ? location : throw new IOException(error ?? "Enter the server URL.");

    public static string FromLegacy(ConnectionProfile profile)
    {
        var port = profile.Port ?? 443;
        var scheme = port == 80 ? "http" : "https";
        var path = profile.InitialPath.Trim();
        if (!path.StartsWith('/')) path = "/";
        return Compose(scheme, profile.Host.Trim(), port, NormalizePath(path, path.EndsWith('/')));
    }

    public static string NormalizedServerUrl(string serverUrl)
    {
        if (!Parse(serverUrl, out var uri, out _)) return serverUrl.Trim();
        return Compose(uri.Scheme, Host(uri), uri.Port, NormalizePath(Unescape(uri.AbsolutePath), KeepTrailingSlash(serverUrl)));
    }

    private static bool Parse(string? text, out Uri uri, out string? error)
    {
        uri = null!;
        error = null;
        var value = (text ?? "").Trim();
        if (value.Length == 0) { error = "Enter the server URL."; return false; }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("https" or "http"))
        { error = "Enter an http or https server URL."; return false; }
        if (!string.IsNullOrEmpty(parsed.UserInfo)) { error = "Put the user name in its own field, not in the URL."; return false; }
        if (string.IsNullOrWhiteSpace(parsed.IdnHost)) { error = "Enter a server URL with a host name."; return false; }
        uri = parsed;
        return true;
    }

    private static bool IsUserSegment(string username)
    {
        var value = username.Trim();
        return value.Length > 0 && value is not ("." or "..") && value.IndexOfAny(['/', '\\']) < 0 && !value.Any(char.IsControl);
    }

    private static string ServicePath(Uri server, string suffix)
    {
        var basePath = NormalizePath(Unescape(server.AbsolutePath), keepTrailingSlash: false);
        var prefix = basePath == "/" ? "" : basePath;
        return prefix + "/" + suffix.Trim('/') + "/";
    }

    private static string Root(string path)
    {
        var text = NormalizePath(path, keepTrailingSlash: false);
        return text.Length == 0 ? "/" : text;
    }

    private static bool KeepTrailingSlash(string? url)
    {
        var path = (url ?? "").Trim();
        var scheme = path.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) path = path[(scheme + 3)..];
        var slash = path.IndexOf('/');
        if (slash < 0) return true;
        path = path[slash..];
        var query = path.IndexOfAny(['?', '#']);
        if (query >= 0) path = path[..query];
        return path.EndsWith('/');
    }

    internal static string NormalizePath(string path, bool keepTrailingSlash)
    {
        var trailing = keepTrailingSlash && path.Trim().EndsWith('/');
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        if (parts.Count == 0) return "/";
        var text = "/" + string.Join('/', parts);
        return trailing ? text + "/" : text;
    }

    private static string Compose(string scheme, string host, int port, string path)
    {
        var authority = port is 443 && scheme == "https" || port is 80 && scheme == "http"
            ? $"{scheme}://{FormatHost(host)}"
            : $"{scheme}://{FormatHost(host)}:{port}";
        if (path is "/" or "") return authority + "/";
        var trailing = path.EndsWith('/');
        var encoded = "/" + string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return authority + encoded + (trailing ? "/" : "");
    }

    private static string Host(Uri uri) => uri.IdnHost;

    private static string FormatHost(string host) => host.Contains(':') ? $"[{host}]" : host;

    private static string Unescape(string path) => Uri.UnescapeDataString(path);
}
