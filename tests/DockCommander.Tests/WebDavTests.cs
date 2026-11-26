using System.Net;
using System.Text;
using DockCommander.App;
using DockCommander.Core.Connections;
using DockCommander.Core.Files;
using DockCommander.Core.Operations;
using Xunit;

#pragma warning disable xUnit1051

namespace DockCommander.Tests;

public sealed class WebDavTests
{
    [Fact]
    public void HttpsIsTheDefaultAndPort80StaysPlain()
    {
        var secure = WebDavRemote.Origin(new ConnectionProfile("a", "Cloud", "WebDAV", "cloud.example.com"));
        var plain = WebDavRemote.Origin(new ConnectionProfile("a", "Cloud", "WebDAV", "cloud.example.com", 80));
        Assert.Equal("https", secure.Scheme);
        Assert.Equal(443, secure.Port);
        Assert.Equal("http", plain.Scheme);
        Assert.Equal(80, plain.Port);
        Assert.Equal("/remote.php/dav/files/denis", WebDavRemote.Folder(
            new ConnectionProfile("a", "Cloud", "WebDAV", "cloud.example.com", InitialPath: "https://cloud.example.com/remote.php/dav/files/denis")));
    }

    [Fact]
    public void DigestChallengeIsAnsweredWithTheSharedSecret()
    {
        var challenge = WebDavDigest.Parse("realm=\"Cloud\", nonce=\"abc\", qop=\"auth\", algorithm=MD5, opaque=\"zz\"");
        var header = WebDavDigest.Header("denis", "secret", "PROPFIND", "/dav", challenge, 1);
        Assert.Contains("username=\"denis\"", header, StringComparison.Ordinal);
        Assert.Contains("nonce=\"abc\"", header, StringComparison.Ordinal);
        Assert.Contains("uri=\"/dav\"", header, StringComparison.Ordinal);
        Assert.Contains("opaque=\"zz\"", header, StringComparison.Ordinal);
        Assert.Contains("qop=auth", header, StringComparison.Ordinal);
        Assert.Contains("nc=00000001", header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListsReadsWritesAndRenamesThroughTheProvider()
    {
        using var server = new MemoryDav();
        server.AddDirectory("/dav");
        server.AddFile("/dav/notes.txt", "hello");
        using var remote = new WebDavRemote(server, new Uri("https://cloud.example.com:443/"), "denis", "secret");
        var pane = new SftpFileSystemProvider(remote, "cloud", "Cloud", "/dav", "webdav");
        var listed = await pane.ListAsync("/dav");
        Assert.Contains(listed, entry => entry.Name == "notes.txt" && !entry.IsDirectory && entry.Size == 5);
        await using (var read = await pane.OpenReadAsync("/dav/notes.txt"))
        using (var text = new StreamReader(read))
            Assert.Equal("hello", await text.ReadToEndAsync());

        using var temp = new ScratchDirectory();
        var local = new LocalFileSystemProvider();
        var source = temp.File("out.txt", "next");
        await new FileOperationService().TransferAsync(local, pane, [(await local.GetEntryAsync(source))!], "/dav", false,
            _ => Task.FromResult(new ConflictResolution(ConflictAction.Overwrite)));
        Assert.Equal("next", server.Read("/dav/out.txt"));
        await pane.CreateDirectoryAsync("/dav/my folder");
        var renamed = await pane.RenameAsync("/dav/notes.txt", "renamed notes.txt");
        Assert.Equal("/dav/renamed notes.txt", pane.DisplayPath(renamed));
        await pane.DeleteAsync("/dav/my folder", DeleteMode.Permanent);
        Assert.Null(await pane.GetEntryAsync("/dav/my folder"));
        using var other = new SftpFileSystemProvider(new MemorySftp(), "other", "SFTP", "/");
        Assert.False(pane.CanHandle(other.NormalizePath("/dav")));
    }

    [Fact]
    public void ADigestChallengeIsRetriedAndBasicIsSentFirst()
    {
        var server = new ScriptDav(request =>
        {
            if (request.Headers.Authorization?.Scheme == "Digest")
                return Xml(HttpStatusCode.MultiStatus, Collection("/dav"));
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Headers = { WwwAuthenticate = { new System.Net.Http.Headers.AuthenticationHeaderValue("Digest", "realm=\"Cloud\", nonce=\"n1\", qop=\"auth\"") } }
            };
        });
        using var remote = new WebDavRemote(server, new Uri("https://cloud.example.com:443/"), "denis", "secret");
        var entries = remote.List("/dav", CancellationToken.None);
        Assert.Empty(entries);
        Assert.Equal("Basic", server.Requests[0].Headers.Authorization?.Scheme);
        Assert.Equal("Digest", server.Requests[1].Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task PresetRootListsTransfersAndRenamesAtTheCollection()
    {
        const string root = "/remote.php/dav/files/denis";
        using var server = new MemoryDav();
        server.AddDirectory(root);
        server.AddFile(root + "/notes.txt", "hello");
        using var remote = new WebDavRemote(server, new Uri("https://cloud.example.com/"), "denis", "secret", root);
        var pane = new SftpFileSystemProvider(remote, "cloud", "Cloud", "/", "webdav");
        var listed = await pane.ListAsync("/");
        var notes = Assert.Single(listed);
        Assert.Equal("notes.txt", notes.Name);
        Assert.Equal(5, notes.Size);
        await using (var read = await pane.OpenReadAsync(notes.FullPath))
        using (var text = new StreamReader(read))
            Assert.Equal("hello", await text.ReadToEndAsync());

        using var temp = new ScratchDirectory();
        var local = new LocalFileSystemProvider();
        var service = new FileOperationService();
        var source = temp.File("out.txt", "next");
        await service.TransferAsync(local, pane, [(await local.GetEntryAsync(source))!], "/", false,
            _ => Task.FromResult(new ConflictResolution(ConflictAction.Overwrite)));
        Assert.Equal("next", server.Read(root + "/out.txt"));

        var remoteFile = Assert.Single(await pane.ListAsync("/"), entry => entry.Name == "notes.txt");
        await service.TransferAsync(pane, local, [remoteFile], temp.Root, false,
            _ => Task.FromResult(new ConflictResolution(ConflictAction.Overwrite)));
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(temp.Root, "notes.txt")));

        var otherRemote = new MemorySftp();
        otherRemote.AddDirectory("/");
        otherRemote.AddFile("/a.txt", "from-sftp");
        using var sftp = new SftpFileSystemProvider(otherRemote, "other", "SFTP", "/");
        var incoming = (await sftp.GetEntryAsync(sftp.NormalizePath("/a.txt")))!;
        await service.TransferAsync(sftp, pane, [incoming], "/", false,
            _ => Task.FromResult(new ConflictResolution(ConflictAction.Overwrite)));
        Assert.Equal("from-sftp", server.Read(root + "/a.txt"));
        await pane.CreateDirectoryAsync(pane.CombinePath("/", "folder"));
        await pane.DeleteAsync(pane.CombinePath("/", "folder"), DeleteMode.Permanent);
    }

    [Fact]
    public void OwnCloudFallsBackToTheClassicEndpoint()
    {
        var server = new ScriptDav(request =>
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            if (path.Contains("/remote.php/dav/files/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (path.Contains("/remote.php/webdav", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.MultiStatus);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var profile = new ConnectionProfile("cloud", "Cloud", ConnectionProtocols.WebDav, "cloud.example.com",
            Username: "denis", WebDavPreset: WebDavPresets.OwnCloud, ServerUrl: "https://cloud.example.com/");
        using var remote = WebDavRemote.Open(profile, new SftpCredentials(AuthenticationKinds.Password, "secret", "", ""),
            (_, _) => true, server, CancellationToken.None);
        Assert.Equal("https://cloud.example.com/remote.php/webdav/", remote.EffectiveUrl);
        Assert.Contains(server.Requests, request => request.RequestUri!.AbsolutePath.Contains("/remote.php/dav/files/", StringComparison.Ordinal));
        Assert.Contains(server.Requests, request => request.RequestUri!.AbsolutePath.Contains("/remote.php/webdav", StringComparison.Ordinal));
    }

    private sealed class ScriptDav(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond(request);
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    private static HttpResponseMessage Xml(HttpStatusCode status, string body)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml")
        };
        return response;
    }

    private static string Collection(string path) => $"""
        <d:multistatus xmlns:d="DAV:">
          <d:response><d:href>{path}</d:href><d:propstat><d:status>HTTP/1.1 200 OK</d:status><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
        </d:multistatus>
        """;

    private sealed class MemoryDav : HttpMessageHandler, IDisposable
    {
        private readonly Dictionary<string, byte[]?> _nodes = new(StringComparer.Ordinal) { ["/"] = null };

        public void AddDirectory(string path) => _nodes[Clean(path)] = null;

        public void AddFile(string path, string text) => _nodes[Clean(path)] = Encoding.UTF8.GetBytes(text);

        public string Read(string path) => Encoding.UTF8.GetString(_nodes[Clean(path)] ?? throw new InvalidOperationException());

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Clean(Uri.UnescapeDataString(request.RequestUri!.AbsolutePath));
            var method = request.Method.Method;
            if (method == "PROPFIND") return Propfind(path, request.Headers.GetValues("Depth").First() != "0");
            if (method == "GET")
            {
                if (!_nodes.TryGetValue(path, out var content) || content is null) return new HttpResponseMessage(HttpStatusCode.NotFound);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
            }
            if (method == "PUT")
            {
                _nodes[path] = request.Content!.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            if (method == "MKCOL")
            {
                if (_nodes.ContainsKey(path)) return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
                _nodes[path] = null;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            if (method == "DELETE")
            {
                if (!_nodes.Remove(path)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                foreach (var child in _nodes.Keys.Where(item => item.StartsWith(path + "/", StringComparison.Ordinal)).ToArray())
                    _nodes.Remove(child);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (method == "MOVE")
            {
                var destination = Clean(Uri.UnescapeDataString(new Uri(request.Headers.GetValues("Destination").First()).AbsolutePath));
                if (!_nodes.TryGetValue(path, out var content)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                _nodes.Remove(path);
                _nodes[destination] = content;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));

        private HttpResponseMessage Propfind(string path, bool children)
        {
            if (!_nodes.ContainsKey(path)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var rows = new StringBuilder();
            rows.Append(Response(path, _nodes[path] is null));
            if (children)
            {
                foreach (var item in _nodes.Where(item => Parent(item.Key) == path))
                    rows.Append(Response(item.Key, item.Value is null));
            }
            return Xml(HttpStatusCode.MultiStatus, $"<d:multistatus xmlns:d=\"DAV:\">{rows}</d:multistatus>");
        }

        private string Response(string path, bool directory)
        {
            var kind = directory ? "<d:collection/>" : "";
            var length = directory ? "" : $"<d:getcontentlength>{_nodes[path]!.Length}</d:getcontentlength>";
            var href = string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
            return $"<d:response><d:href>{href}</d:href><d:propstat><d:status>HTTP/1.1 200 OK</d:status><d:prop><d:resourcetype>{kind}</d:resourcetype>{length}</d:prop></d:propstat></d:response>";
        }

        private static string Parent(string path)
        {
            if (path == "/") return "";
            var slash = path.LastIndexOf('/');
            return slash <= 0 ? "/" : path[..slash];
        }

        private static string Clean(string path)
        {
            if (string.IsNullOrEmpty(path)) return "/";
            if (!path.StartsWith('/')) path = "/" + path;
            return path.Length > 1 ? path.TrimEnd('/') : path;
        }
    }
}
