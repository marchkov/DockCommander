using System.Text.Json.Serialization;

namespace DockCommander.Core.Connections;

public static class ConnectionProtocols
{
    public const string Sftp = "SFTP";
    public const string Ftp = "FTP";
    public const string Ftps = "FTPS";
    public const string WebDav = "WebDAV";
    public static readonly IReadOnlyList<string> All = [Sftp, Ftp, Ftps, WebDav];

    public static int? DefaultPort(string protocol) => protocol switch
    {
        Sftp => 22,
        Ftp or Ftps => 21,
        WebDav => 443,
        _ => null
    };
}

public static class AuthenticationKinds
{
    public const string Password = "Password";
    public const string PrivateKey = "Private key";
    public const string Anonymous = "Anonymous";
    public static readonly IReadOnlyList<string> All = [Password, PrivateKey, Anonymous];
}

public sealed record ConnectionProfile(
    string Id,
    string Name,
    string Protocol,
    string Host,
    int? Port = null,
    string Username = "",
    string InitialPath = "",
    string Authentication = AuthenticationKinds.Password,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? HostFingerprint = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? WebDavPreset = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ServerUrl = null)
{
    public string Detail
    {
        get
        {
            var host = Host.Trim();
            var address = Port is int port ? $"{host}:{port}" : host;
            var preset = Protocol.Trim().Equals(ConnectionProtocols.WebDav, StringComparison.OrdinalIgnoreCase) &&
                WebDavPresets.IsKnown(WebDavPreset) ? $" · {WebDavPreset!.Trim()}" : "";
            return $"{Protocol.Trim()}{preset} · {address}";
        }
    }

    public ConnectionProfile Normalized() => this with
    {
        Name = Name.Trim(),
        Protocol = Protocol.Trim(),
        Host = Host.Trim(),
        Username = Username.Trim(),
        InitialPath = InitialPath.Trim(),
        Authentication = Authentication.Trim(),
        HostFingerprint = string.IsNullOrWhiteSpace(HostFingerprint) ? null : HostFingerprint.Trim(),
        WebDavPreset = Protocol.Trim().Equals(ConnectionProtocols.WebDav, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(WebDavPreset) ? WebDavPreset.Trim() : null,
        ServerUrl = Protocol.Trim().Equals(ConnectionProtocols.WebDav, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(ServerUrl) ? ServerUrl.Trim() : null
    };

    public bool Trusts(string? fingerprint) =>
        !string.IsNullOrWhiteSpace(HostFingerprint) &&
        string.Equals(HostFingerprint, fingerprint, StringComparison.Ordinal);

    public static string? Validate(ConnectionProfile profile)
    {
        profile = profile.Normalized();
        if (string.IsNullOrWhiteSpace(profile.Id)) return "A connection needs an id.";
        if (string.IsNullOrWhiteSpace(profile.Name)) return "Enter a connection name.";
        if (!ConnectionProtocols.All.Contains(profile.Protocol)) return "Choose SFTP, FTP, FTPS, or WebDAV.";
        if (string.IsNullOrWhiteSpace(profile.Host) || profile.Host.Any(char.IsWhiteSpace) || profile.Host.Contains('/'))
            return "Enter a host name or address, without a path.";
        if (profile.Port is < 1 or > 65535) return "Port must be between 1 and 65535.";
        if (!AuthenticationKinds.All.Contains(profile.Authentication)) return "Choose an authentication method.";
        if (WebDavLocations.Validate(profile) is { } webDav) return webDav;
        return null;
    }
}
