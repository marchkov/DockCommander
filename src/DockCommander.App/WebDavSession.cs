using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DockCommander.Core.Connections;
using DockCommander.Core.Files;

namespace DockCommander.App;

internal interface IWebDavConnector
{
    Task<IFileSystemProvider> ConnectAsync(ConnectionProfile profile, SftpCredentials credentials,
        Func<string, bool, bool> trustCertificate, CancellationToken cancellationToken);
}

internal sealed class WebDavConnector : IWebDavConnector
{
    public Task<IFileSystemProvider> ConnectAsync(ConnectionProfile profile, SftpCredentials credentials,
        Func<string, bool, bool> trustCertificate, CancellationToken cancellationToken) => Task.Run<IFileSystemProvider>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remote = WebDavRemote.Open(profile, credentials, trustCertificate, cancellationToken);
        var folder = WebDavLocations.UsesPreset(profile.Normalized()) ? "/" : WebDavRemote.Folder(profile);
        return new SftpFileSystemProvider(remote, profile.Id, profile.Name, folder, "webdav");
    }, cancellationToken);
}

internal sealed class WebDavRemote : ISftpRemote
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly byte[] PropfindBody = Encoding.UTF8.GetBytes("""
        <?xml version="1.0" encoding="utf-8"?>
        <d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/></d:prop></d:propfind>
        """);
    private readonly HttpClient _client;
    private readonly Uri _origin;
    private readonly string _username;
    private readonly string _password;
    private readonly bool _anonymous;
    private readonly string _root;
    private readonly bool _requireRoot;
    private readonly string _effectiveUrl;
    private readonly object _auth = new();
    private Dictionary<string, string>? _digest;
    private int _nonceCount;
    private int _disposed;

    private WebDavRemote(HttpClient client, Uri origin, string username, string password, bool anonymous,
        string root, bool requireRoot, string effectiveUrl)
    {
        _client = client;
        _origin = origin;
        _username = username;
        _password = password;
        _anonymous = anonymous;
        _root = Clean(string.IsNullOrWhiteSpace(root) ? "/" : root);
        _requireRoot = requireRoot;
        _effectiveUrl = effectiveUrl;
    }

    internal WebDavRemote(HttpMessageHandler handler, Uri origin, string username, string password, string root = "/")
        : this(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, origin, username, password, false,
            root, false, origin.AbsoluteUri) { }

    public static Uri Origin(ConnectionProfile profile)
    {
        var port = profile.Port ?? 443;
        return new UriBuilder(port == 80 ? "http" : "https", profile.Host.Trim(), port).Uri;
    }

    public static string Folder(ConnectionProfile profile)
    {
        var text = profile.InitialPath.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && absolute.IsAbsoluteUri)
            return string.IsNullOrEmpty(absolute.AbsolutePath) ? "/" : Uri.UnescapeDataString(absolute.AbsolutePath);
        return text;
    }

    public static WebDavRemote Open(ConnectionProfile profile, SftpCredentials credentials,
        Func<string, bool, bool> trustCertificate, CancellationToken cancellationToken) =>
        Open(profile, credentials, trustCertificate, null, cancellationToken);

    internal static WebDavRemote Open(ConnectionProfile profile, SftpCredentials credentials,
        Func<string, bool, bool> trustCertificate, HttpMessageHandler? handler, CancellationToken cancellationToken)
    {
        profile = profile.Normalized();
        var preset = WebDavLocations.UsesPreset(profile);
        var location = preset ? WebDavLocations.Resolve(profile) : Legacy(profile);
        var remote = Create(profile, credentials, trustCertificate, location, preset, handler, cancellationToken);
        try
        {
            var status = remote.Probe(cancellationToken);
            if (status == 404 && profile.WebDavPreset == WebDavPresets.OwnCloud)
            {
                var modern = remote.EffectiveUrl;
                remote.Dispose();
                var classic = WebDavLocations.Resolve(profile, ownCloudClassic: true);
                remote = Create(profile, credentials, trustCertificate, classic, true, handler, cancellationToken);
                status = remote.Probe(cancellationToken);
                if (status == 404)
                    throw new IOException($"ownCloud WebDAV was not found at {modern} or {remote.EffectiveUrl}.");
            }
            if (remote._requireRoot && status == 404)
                throw new IOException($"The WebDAV collection was not found at {remote.EffectiveUrl}.");
            return remote;
        }
        catch
        {
            remote.Dispose();
            throw;
        }
    }

    internal string EffectiveUrl => _effectiveUrl;

    private static WebDavLocation Legacy(ConnectionProfile profile)
    {
        var origin = Origin(profile);
        return new WebDavLocation(origin.Scheme, origin.Host, origin.Port, "/", origin.GetLeftPart(UriPartial.Authority) + "/");
    }

    private static WebDavRemote Create(ConnectionProfile profile, SftpCredentials credentials,
        Func<string, bool, bool> trustCertificate, WebDavLocation location, bool requireRoot,
        HttpMessageHandler? sharedHandler, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HttpMessageHandler handler;
        var disposeHandler = false;
        if (sharedHandler is null)
        {
            var sockets = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(20),
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None
            };
            if (location.Scheme == "https")
            {
                sockets.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    var thumb = certificate?.GetCertHashString()?.Replace(" ", "").ToUpperInvariant() ?? "";
                    if (thumb.Length == 0) return errors == SslPolicyErrors.None;
                    return trustCertificate(thumb, errors == SslPolicyErrors.None);
                };
            }
            handler = sockets;
            disposeHandler = true;
        }
        else handler = sharedHandler;
        var anonymous = profile.Authentication == AuthenticationKinds.Anonymous;
        var origin = new UriBuilder(location.Scheme, location.Host, location.Port).Uri;
        return new WebDavRemote(new HttpClient(handler, disposeHandler) { Timeout = Timeout.InfiniteTimeSpan }, origin,
            anonymous ? "" : profile.Username, anonymous ? "" : credentials.Password, anonymous,
            location.RootPath, requireRoot, location.EffectiveUrl);
    }

    public IReadOnlyList<SftpRemoteEntry> List(string path, CancellationToken cancellationToken)
    {
        using var response = Propfind(path, "1", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new IOException("The folder was not found.");
        EnsureSuccess(response);
        var self = Clean(path);
        return Parse(Read(response)).Select(Map).Where(entry => entry is not null && !string.Equals(entry.Path, self, StringComparison.Ordinal))
            .Cast<SftpRemoteEntry>().ToArray();
    }

    public SftpRemoteEntry? TryStat(string path, CancellationToken cancellationToken)
    {
        using var response = Propfind(path, "0", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureSuccess(response);
        return Parse(Read(response)).Select(Map).FirstOrDefault(entry => entry is not null);
    }

    public Stream OpenRead(string path, CancellationToken cancellationToken)
    {
        var response = Send(HttpMethod.Get, path, null, cancellationToken);
        try
        {
            if (response.StatusCode == HttpStatusCode.NotFound) throw new FileNotFoundException("The file is no longer there.");
            EnsureSuccess(response);
            return new ResponseStream(response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public Stream OpenWrite(string path, CancellationToken cancellationToken) => new UploadStream(this, path, cancellationToken);

    public void CreateDirectory(string path, CancellationToken cancellationToken)
    {
        using var response = Send(new HttpMethod("MKCOL"), path, null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
            throw new IOException("A folder with this name already exists.");
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new IOException("The parent folder was not found.");
        EnsureSuccess(response);
    }

    public void DeleteFile(string path, CancellationToken cancellationToken) => Delete(path, cancellationToken);

    public void DeleteDirectory(string path, CancellationToken cancellationToken) => Delete(path, cancellationToken);

    public void Rename(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var destination = Resource(destinationPath);
        using var response = Send(new HttpMethod("MOVE"), sourcePath, null, cancellationToken, new Dictionary<string, string>
        {
            ["Destination"] = destination.AbsoluteUri,
            ["Overwrite"] = "F"
        });
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Forbidden)
            throw new IOException("An entry with this name already exists.");
        if (response.StatusCode == HttpStatusCode.NotFound) throw new FileNotFoundException("The item is no longer there.");
        EnsureSuccess(response);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _client.Dispose();
    }

    private int Probe(CancellationToken cancellationToken)
    {
        using var response = Propfind("/", "0", cancellationToken);
        var code = (int)response.StatusCode;
        if (code is 301 or 302 or 303 or 307 or 308)
            throw new IOException(_requireRoot
                ? "The server redirected this address. Check the server URL."
                : "The server redirected this address. Use HTTPS, or put the WebDAV collection in Initial path.");
        if (code >= 500) throw new IOException($"The server returned {code} {response.ReasonPhrase}.");
        return code;
    }

    private void Delete(string path, CancellationToken cancellationToken)
    {
        using var response = Send(HttpMethod.Delete, path, null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new FileNotFoundException("The item is no longer there.");
        EnsureSuccess(response);
    }

    private HttpResponseMessage Propfind(string path, string depth, CancellationToken cancellationToken) =>
        Send(new HttpMethod("PROPFIND"), path, PropfindBody, cancellationToken, new Dictionary<string, string> { ["Depth"] = depth });

    private HttpResponseMessage Send(HttpMethod method, string path, byte[]? body, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers = null) =>
        Send(method, Resource(path), body, cancellationToken, headers, allowDigestRetry: true);

    private HttpResponseMessage Send(HttpMethod method, Uri resource, byte[]? body, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers, bool allowDigestRetry)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(method, resource);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml") { CharSet = "utf-8" };
        }
        foreach (var header in headers ?? new Dictionary<string, string>())
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        ApplyAuth(request);
        HttpResponseMessage response;
        try { response = _client.Send(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException("The connection was cancelled.", ex, cancellationToken);
            throw new IOException(ex.Message, ex);
        }
        if (allowDigestRetry && response.StatusCode == HttpStatusCode.Unauthorized && AcceptDigest(response, resource))
        {
            response.Dispose();
            return Send(method, resource, body, cancellationToken, headers, allowDigestRetry: false);
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            throw new IOException("The server rejected the sign-in.");
        }
        return response;
    }

    internal HttpResponseMessage Put(Uri resource, Stream content, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, resource) { Content = new StreamContent(content) };
        request.Headers.ExpectContinue = false;
        ApplyAuth(request);
        try
        {
            var response = _client.Send(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            request.Dispose();
            return response;
        }
        catch (OperationCanceledException) { request.Dispose(); throw; }
        catch (Exception ex)
        {
            request.Dispose();
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException("The connection was cancelled.", ex, cancellationToken);
            throw new IOException(ex.Message, ex);
        }
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (_anonymous || string.IsNullOrEmpty(_username)) return;
        lock (_auth)
        {
            if (_digest is { } challenge)
            {
                _nonceCount++;
                request.Headers.Authorization = new AuthenticationHeaderValue("Digest",
                    WebDavDigest.Header(_username, _password, request.Method.Method, request.RequestUri!.PathAndQuery, challenge, _nonceCount));
                return;
            }
        }
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(_username + ":" + _password));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    private bool AcceptDigest(HttpResponseMessage response, Uri resource)
    {
        foreach (var header in response.Headers.WwwAuthenticate)
        {
            if (!header.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(header.Parameter))
                continue;
            var challenge = WebDavDigest.Parse(header.Parameter);
            if (!challenge.ContainsKey("realm") || !challenge.ContainsKey("nonce")) continue;
            lock (_auth)
            {
                _digest = challenge;
                _nonceCount = 0;
            }
            _ = resource;
            return true;
        }
        return false;
    }

    private Uri Resource(string path)
    {
        var clean = ServerPath(path);
        var encoded = string.Join("/", clean.Split('/', StringSplitOptions.None)
            .Select(segment => segment.Length == 0 ? "" : Uri.EscapeDataString(segment)));
        return new Uri(_origin, encoded);
    }

    private string ServerPath(string path)
    {
        var clean = Clean(path);
        if (_root == "/") return clean;
        return clean == "/" ? _root : _root + clean;
    }

    private SftpRemoteEntry? Map(SftpRemoteEntry entry)
    {
        var path = ToPanePath(entry.Path);
        if (path is null) return null;
        var name = path == "/" ? "/" : path[(path.LastIndexOf('/') + 1)..];
        return entry with { Name = name, Path = path };
    }

    private string? ToPanePath(string serverPath)
    {
        if (_root == "/") return serverPath;
        if (string.Equals(serverPath, _root, StringComparison.OrdinalIgnoreCase)) return "/";
        var prefix = _root + "/";
        if (serverPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return "/" + serverPath[prefix.Length..];
        return null;
    }

    private static string Clean(string path)
    {
        var text = string.IsNullOrWhiteSpace(path) ? "/" : path.Replace('\\', '/');
        if (!text.StartsWith('/')) text = "/" + text;
        if (text.Length > 1) text = text.TrimEnd('/');
        return text;
    }

    private static string Read(HttpResponseMessage response) =>
        response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        if (code is >= 200 and <= 299 or 207) return;
        throw new IOException($"The server returned {code} {response.ReasonPhrase}.");
    }

    private static List<SftpRemoteEntry> Parse(string xml)
    {
        var document = XDocument.Parse(xml);
        var entries = new List<SftpRemoteEntry>();
        foreach (var response in document.Descendants(Dav + "response"))
        {
            var href = response.Element(Dav + "href")?.Value;
            if (string.IsNullOrWhiteSpace(href)) continue;
            var props = response.Elements(Dav + "propstat")
                .FirstOrDefault(item => (item.Element(Dav + "status")?.Value ?? "200").Contains("200", StringComparison.Ordinal))
                ?.Element(Dav + "prop");
            var path = HrefPath(href, out var trailingCollection);
            var directory = trailingCollection
                || props?.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null;
            long? size = null;
            if (!directory && long.TryParse(props?.Element(Dav + "getcontentlength")?.Value, out var parsed)) size = parsed;
            DateTimeOffset? modified = null;
            if (DateTimeOffset.TryParse(props?.Element(Dav + "getlastmodified")?.Value, out var stamp)) modified = stamp;
            var name = path == "/" ? "/" : path[(path.LastIndexOf('/') + 1)..];
            if (name is "." or "..") continue;
            entries.Add(new SftpRemoteEntry(name, path, directory, false, size, modified));
        }
        return entries;
    }

    private static string HrefPath(string href, out bool trailingCollection)
    {
        var text = href.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute)) text = absolute.AbsolutePath;
        text = Uri.UnescapeDataString(text);
        if (!text.StartsWith('/')) text = "/" + text;
        trailingCollection = text.Length > 1 && text.EndsWith('/');
        if (trailingCollection) text = text.TrimEnd('/');
        return text.Length == 0 ? "/" : text;
    }

    private sealed class ResponseStream(HttpResponseMessage response) : Stream
    {
        private readonly Stream _inner = response.Content.ReadAsStream();

        public override bool CanRead => true;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _inner.Dispose(); response.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private sealed class UploadStream : Stream
    {
        private readonly WebDavRemote _remote;
        private readonly Uri _resource;
        private readonly CancellationToken _cancellation;
        private readonly System.IO.Pipelines.Pipe _pipe = new();
        private Task<HttpResponseMessage>? _send;
        private int _disposed;

        public UploadStream(WebDavRemote remote, string path, CancellationToken cancellationToken)
        {
            _remote = remote;
            _resource = remote.Resource(path);
            _cancellation = cancellationToken;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _pipe.Writer.FlushAsync(_cancellation).AsTask().GetAwaiter().GetResult();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _cancellation.ThrowIfCancellationRequested();
            Start();
            var span = _pipe.Writer.GetSpan(count);
            buffer.AsSpan(offset, count).CopyTo(span);
            _pipe.Writer.Advance(count);
            Flush();
        }

        private void Start() => _send ??= Task.Run(() => _remote.Put(_resource, _pipe.Reader.AsStream(), _cancellation), _cancellation);

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Start();
                _pipe.Writer.Complete();
                using var response = _send!.GetAwaiter().GetResult();
                if (response.StatusCode == HttpStatusCode.Unauthorized) throw new IOException("The server rejected the sign-in.");
                EnsureSuccess(response);
            }
            base.Dispose(disposing);
        }
    }
}

internal static class WebDavDigest
{
    public static Dictionary<string, string> Parse(string parameter)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        while (index < parameter.Length)
        {
            while (index < parameter.Length && parameter[index] is ',' or ' ') index++;
            var start = index;
            while (index < parameter.Length && parameter[index] is not '=' and not ',') index++;
            if (index >= parameter.Length || parameter[index] != '=') break;
            var key = parameter[start..index].Trim();
            index++;
            string value;
            if (index < parameter.Length && parameter[index] == '"')
            {
                index++;
                var builder = new StringBuilder();
                while (index < parameter.Length && parameter[index] != '"')
                {
                    if (parameter[index] == '\\' && index + 1 < parameter.Length) { builder.Append(parameter[index + 1]); index += 2; }
                    else builder.Append(parameter[index++]);
                }
                if (index < parameter.Length) index++;
                value = builder.ToString();
            }
            else
            {
                start = index;
                while (index < parameter.Length && parameter[index] != ',') index++;
                value = parameter[start..index].Trim();
            }
            if (key.Length > 0) result[key] = value;
        }
        return result;
    }

    public static string Header(string username, string password, string method, string uri, IReadOnlyDictionary<string, string> challenge, int nonceCount)
    {
        var realm = challenge["realm"];
        var nonce = challenge["nonce"];
        var qop = challenge.TryGetValue("qop", out var offered) && offered.Split(',').Select(item => item.Trim()).Contains("auth", StringComparer.OrdinalIgnoreCase)
            ? "auth" : "";
        var algorithm = challenge.TryGetValue("algorithm", out var named) && named.Length > 0 ? named : "MD5";
        var cnonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var nc = nonceCount.ToString("x8");
        var hash = algorithm.Equals("SHA-256", StringComparison.OrdinalIgnoreCase) ? (Func<string, string>)Sha256 : Md5;
        if (algorithm.Contains("sess", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The server asked for an unsupported digest algorithm.");
        var ha1 = hash($"{username}:{realm}:{password}");
        var ha2 = hash($"{method}:{uri}");
        var response = qop.Length == 0 ? hash($"{ha1}:{nonce}:{ha2}") : hash($"{ha1}:{nonce}:{nc}:{cnonce}:{qop}:{ha2}");
        var header = $"username=\"{Escape(username)}\", realm=\"{Escape(realm)}\", nonce=\"{Escape(nonce)}\", uri=\"{Escape(uri)}\", algorithm={algorithm}, response=\"{response}\"";
        if (qop.Length > 0) header += $", qop={qop}, nc={nc}, cnonce=\"{cnonce}\"";
        if (challenge.TryGetValue("opaque", out var opaque)) header += $", opaque=\"{Escape(opaque)}\"";
        return header;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Md5(string value) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
