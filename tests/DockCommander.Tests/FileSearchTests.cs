using DockCommander.Core.Files;
using Xunit;
#pragma warning disable xUnit1051 // These tests supply their own cancellation token.

namespace DockCommander.Tests;

public sealed class FileSearchTests
{
    [Fact]
    public async Task FindsAFileInTheCurrentDirectoryAndANestedFile()
    {
        var files = Sample();
        var here = await FileSearch.SearchAsync(files, "/", "here.txt");
        var nested = await FileSearch.SearchAsync(files, "/", "b.txt");
        Assert.Equal("here.txt", Assert.Single(here.Hits).Name);
        var hit = Assert.Single(nested.Hits);
        Assert.Equal("/sub", hit.ParentDirectory);
        Assert.Equal("/sub/b.txt", hit.FullPath);
        Assert.False(hit.IsDirectory);
        var folder = Assert.Single((await FileSearch.SearchAsync(files, "/", "sub")).Hits);
        Assert.True(folder.IsDirectory);
        Assert.Equal("/sub", folder.FullPath);
    }

    [Fact]
    public async Task WildcardsMatchStarQuestionAndLists()
    {
        var files = Sample();
        var text = await FileSearch.SearchAsync(files, "/", "*.txt");
        Assert.Equal(["b.txt", "here.txt", "keep.txt", "quarterly-report.txt"], text.Hits.Select(hit => hit.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Single((await FileSearch.SearchAsync(files, "/", "notes?.md")).Hits, hit => hit.Name == "notes1.md");
        Assert.DoesNotContain((await FileSearch.SearchAsync(files, "/", "notes?.md")).Hits, hit => hit.Name == "notes12.md");
        Assert.Single((await FileSearch.SearchAsync(files, "/", "*report*")).Hits, hit => hit.Name == "quarterly-report.txt");
        Assert.Single((await FileSearch.SearchAsync(files, "/", "config.*")).Hits, hit => hit.Name == "config.json");
        Assert.Contains((await FileSearch.SearchAsync(files, "/", "*.*")).Hits, hit => hit.Name == "README");
        var either = await FileSearch.SearchAsync(files, "/", "*.md;*.json");
        Assert.Contains(either.Hits, hit => hit.Name == "notes1.md");
        Assert.Contains(either.Hits, hit => hit.Name == "config.json");
        Assert.DoesNotContain(either.Hits, hit => hit.Name == "here.txt");
    }

    [Fact]
    public async Task CaseFollowsTheProviderComparer()
    {
        var sensitive = new TreeFiles(StringComparer.Ordinal);
        sensitive.Add(new("Notes.txt", "/Notes.txt", false, 1, null, ".txt"), "/");
        Assert.Empty((await FileSearch.SearchAsync(sensitive, "/", "notes.txt")).Hits);
        Assert.Single((await FileSearch.SearchAsync(sensitive, "/", "Notes.txt")).Hits);

        var ignored = new TreeFiles(StringComparer.OrdinalIgnoreCase);
        ignored.Add(new("Notes.txt", "/Notes.txt", false, 1, null, ".txt"), "/");
        Assert.Single((await FileSearch.SearchAsync(ignored, "/", "notes.txt")).Hits);
    }

    [Fact]
    public async Task IgnoresLinksAndContinuesAfterAnInaccessibleDirectory()
    {
        var files = Sample();
        var found = await FileSearch.SearchAsync(files, "/", "*");
        Assert.DoesNotContain(found.Hits, hit => hit.Name is "secret.txt" or "link");
        Assert.Equal(1, found.SkippedDirectories);
        Assert.Contains(found.Hits, hit => hit.Name == "keep.txt");
        Assert.Contains(found.Hits, hit => hit.Name == "here.txt");
    }

    [Fact]
    public async Task CancellationKeepsResultsFoundSoFar()
    {
        var files = new TreeFiles();
        files.Add(new("found.txt", "/found.txt", false, 1, null, ".txt"), "/");
        files.Add(new("slow", "/slow", true, null, null), "/");
        var started = files.Block("/slow");
        using var cancellation = new CancellationTokenSource();
        var pending = FileSearch.SearchAsync(files, "/", "*", null, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        var outcome = await pending;
        Assert.True(outcome.Cancelled);
        Assert.Contains(outcome.Hits, hit => hit.Name == "found.txt");
    }

    [Fact]
    public async Task DeepTreeDoesNotOverflowTheStack()
    {
        var files = Chain(8000);
        var found = await FileSearch.SearchAsync(files, "/", "leaf.txt");
        var hit = Assert.Single(found.Hits);
        Assert.Equal("leaf.txt", hit.Name);
        Assert.False(found.Cancelled);
    }

    [Fact]
    public async Task SearchesARemoteProvider()
    {
        var remote = new MemorySftp();
        remote.AddDirectory("/");
        remote.AddDirectory("/docs");
        remote.AddFile("/docs/readme.txt", "hello");
        using var provider = new SftpFileSystemProvider(remote, "box", "Box", "/");
        var found = await FileSearch.SearchAsync(provider, provider.NormalizePath("/"), "*.txt");
        var hit = Assert.Single(found.Hits);
        Assert.Equal("readme.txt", hit.Name);
        Assert.Equal(provider.NormalizePath("/docs"), hit.ParentDirectory);
        Assert.False(hit.IsDirectory);
    }

    private static TreeFiles Sample()
    {
        var files = new TreeFiles();
        files.Add(new("here.txt", "/here.txt", false, 4, null, ".txt"), "/");
        files.Add(new("sub", "/sub", true, null, null), "/");
        files.Add(new("b.txt", "/sub/b.txt", false, 4, null, ".txt"), "/sub");
        files.Add(new("notes1.md", "/notes1.md", false, 2, null, ".md"), "/");
        files.Add(new("notes12.md", "/notes12.md", false, 2, null, ".md"), "/");
        files.Add(new("quarterly-report.txt", "/quarterly-report.txt", false, 2, null, ".txt"), "/");
        files.Add(new("config.json", "/config.json", false, 2, null, ".json"), "/");
        files.Add(new("README", "/README", false, 2, null), "/");
        files.Add(new("link", "/link", true, null, null, null, true), "/");
        files.Add(new("secret.txt", "/link/secret.txt", false, 9, null, ".txt"), "/link");
        files.Add(new("locked", "/locked", true, null, null), "/");
        files.Deny("/locked");
        files.Add(new("keep.txt", "/open/keep.txt", false, 1, null, ".txt"), "/open");
        files.Add(new("open", "/open", true, null, null), "/");
        return files;
    }

    internal static TreeFiles Chain(int depth)
    {
        var files = new TreeFiles();
        var parent = "/";
        for (var i = 0; i < depth; i++)
        {
            var path = parent == "/" ? "/" + i : parent + "/" + i;
            files.Add(new(i.ToString(), path, true, null, null), parent);
            parent = path;
        }
        files.Add(new("leaf.txt", parent + "/leaf.txt", false, 5, null, ".txt"), parent);
        return files;
    }
}

public sealed class FolderSizeTests
{
    [Fact]
    public async Task SumsFilesAndSkipsLinksAndUnknownSizes()
    {
        var empty = await FolderSizeCalculator.MeasureAsync(new TreeFiles(), "/");
        Assert.Equal(0, empty.Bytes);
        Assert.False(empty.Partial);

        var one = new TreeFiles();
        one.Add(new("a.txt", "/a.txt", false, 4, null, ".txt"), "/");
        Assert.Equal(new FolderSize(4, false), await FolderSizeCalculator.MeasureAsync(one, "/"));

        var nested = new TreeFiles();
        nested.Add(new("a.txt", "/a.txt", false, 1, null, ".txt"), "/");
        nested.Add(new("sub", "/sub", true, null, null), "/");
        nested.Add(new("b.txt", "/sub/b.txt", false, 2, null, ".txt"), "/sub");
        nested.Add(new("c.txt", "/sub/c.txt", false, 3, null, ".txt"), "/sub");
        Assert.Equal(new FolderSize(6, false), await FolderSizeCalculator.MeasureAsync(nested, "/"));

        var linked = new TreeFiles();
        linked.Add(new("real.txt", "/real.txt", false, 5, null, ".txt"), "/");
        linked.Add(new("link", "/link", true, null, null, null, true), "/");
        linked.Add(new("secret.txt", "/link/secret.txt", false, 100, null, ".txt"), "/link");
        Assert.Equal(new FolderSize(5, false), await FolderSizeCalculator.MeasureAsync(linked, "/"));

        var unknown = new TreeFiles();
        unknown.Add(new("known.txt", "/known.txt", false, 4, null, ".txt"), "/");
        unknown.Add(new("missing.txt", "/missing.txt", false, null, null, ".txt"), "/");
        var partial = await FolderSizeCalculator.MeasureAsync(unknown, "/");
        Assert.Equal(4, partial.Bytes);
        Assert.True(partial.Partial);

        var denied = new TreeFiles();
        denied.Add(new("known.txt", "/known.txt", false, 4, null, ".txt"), "/");
        denied.Add(new("locked", "/locked", true, null, null), "/");
        denied.Deny("/locked");
        Assert.True((await FolderSizeCalculator.MeasureAsync(denied, "/")).Partial);

        var recovered = new TreeFiles();
        recovered.Add(new("a.txt", "/a.txt", false, null, null, ".txt"), "/");
        recovered.Stat("/a.txt", 8);
        Assert.Equal(new FolderSize(8, false), await FolderSizeCalculator.MeasureAsync(recovered, "/"));
    }

    [Fact]
    public async Task CancellationAndADeepTreeStayIterative()
    {
        var files = new TreeFiles();
        files.Add(new("slow", "/slow", true, null, null), "/");
        var started = files.Block("/slow");
        using var cancellation = new CancellationTokenSource();
        var pending = FolderSizeCalculator.MeasureAsync(files, "/", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        var deep = await FolderSizeCalculator.MeasureAsync(FileSearchTests.Chain(8000), "/");
        Assert.Equal(new FolderSize(5, false), deep);
    }

    [Fact]
    public async Task MeasuresARemoteProvider()
    {
        var remote = new MemorySftp();
        remote.AddDirectory("/");
        remote.AddFile("/readme.txt", "hello");
        using var provider = new SftpFileSystemProvider(remote, "box", "Box", "/");
        var size = await FolderSizeCalculator.MeasureAsync(provider, provider.NormalizePath("/"));
        Assert.Equal(new FolderSize(5, false), size);
    }

    [Fact]
    public void RepeatedCalculationDoesNotStartTheSameDirectoryTwice()
    {
        var gate = new FolderSizeGate();
        Assert.True(gate.TryBegin("/docs", StringComparer.Ordinal));
        Assert.False(gate.TryBegin("/docs", StringComparer.Ordinal));
        Assert.True(gate.TryBegin("/other", StringComparer.Ordinal));
        gate.End();
        Assert.True(gate.TryBegin("/other", StringComparer.Ordinal));
    }

    [Fact]
    public async Task FindFilesUsesTheDirectoryComparer()
    {
        var files = new TreeFiles(StringComparer.Ordinal);
        files.Add(new("Alpha.txt", "/Alpha.txt", false, 1, null), "/");
        files.NameComparer = _ => StringComparer.Ordinal;
        Assert.Empty((await FileSearch.SearchAsync(files, "/", "a*")).Hits);
        files.NameComparer = _ => StringComparer.OrdinalIgnoreCase;
        Assert.Equal("Alpha.txt", Assert.Single((await FileSearch.SearchAsync(files, "/", "a*")).Hits).Name);
    }
}

internal sealed class TreeFiles : IFileSystemProvider
{
    private readonly Dictionary<string, List<FileEntry>> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _stats = new(StringComparer.Ordinal);
    private readonly HashSet<string> _denied = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _blocked = new(StringComparer.Ordinal);

    public TreeFiles(StringComparer? comparer = null) => PathComparer = comparer ?? StringComparer.Ordinal;

    public void Add(FileEntry entry, string parent)
    {
        if (!_children.TryGetValue(parent, out var list)) _children[parent] = list = [];
        list.Add(entry);
    }

    public void Deny(string path) => _denied.Add(path);

    public void Stat(string path, long size) => _stats[path] = size;

    public TaskCompletionSource Block(string path)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _blocked[path] = started;
        return started;
    }

    public string Id => "tree";
    public string DisplayName => "Tree";
    public bool SupportsRecycle => false;
    public StringComparer PathComparer { get; }
    public Func<string, StringComparer>? NameComparer { get; set; }
    public StringComparer ComparerFor(string path) => NameComparer?.Invoke(path) ?? PathComparer;
    public bool CanHandle(string path) => true;
    public string NormalizePath(string path) => path;
    public string CombinePath(string directory, string name) => directory.TrimEnd('/') + "/" + name;
    public bool IsSameOrDescendant(string path, string ancestor) => true;
    public Task<IReadOnlyList<LocationEntry>> GetLocationsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LocationEntry>>([]);

    public async Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_denied.Contains(path)) throw new IOException("Access denied.");
        if (_blocked.TryGetValue(path, out var started))
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        return _children.TryGetValue(path, out var list) ? list : [];
    }

    public Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult<FileEntry?>(_stats.TryGetValue(path, out var size)
            ? new FileEntry(path[(path.LastIndexOf('/') + 1)..], path, false, size, null) : null);
    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteAsync(string path, DeleteMode mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task RemoveEmptyDirectoryAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task MoveFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
