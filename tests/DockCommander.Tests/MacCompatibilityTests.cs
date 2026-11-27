using DockCommander.App;
using DockCommander.Core.Files;
using DockCommander.Core.Operations;
using Xunit;

#pragma warning disable xUnit1051

namespace DockCommander.Tests;

public sealed class MacCompatibilityTests
{
    [Theory]
    [InlineData("/tmp", "/private/tmp")]
    [InlineData("/tmp/a", "/private/tmp/a")]
    [InlineData("/var", "/private/var")]
    [InlineData("/var/folders/x", "/private/var/folders/x")]
    [InlineData("/etc", "/private/etc")]
    [InlineData("/etc/hosts", "/private/etc/hosts")]
    public void VerifiedSystemAliasesRewriteOnlyTheirOwnPrefix(string path, string canonical)
    {
        string? Target(string alias) => alias switch
        {
            "/tmp" => "/private/tmp",
            "/var" => "/private/var",
            "/etc" => "/private/etc/",
            _ => "/somewhere-else"
        };
        Assert.Equal(canonical, MacSystemPaths.Canonicalize(path, Target));
    }

    [Theory]
    [InlineData("/tmp2/a")]
    [InlineData("/temporary")]
    [InlineData("/varfoo")]
    [InlineData("/private/tmp/a")]
    [InlineData("/Users/me/file")]
    [InlineData("/Volumes/Work/tmp/a")]
    public void PathsOutsideTheSystemAliasesStayUnchanged(string path)
    {
        var calls = 0;
        var result = MacSystemPaths.Canonicalize(path, _ =>
        {
            calls++;
            return "/private/tmp";
        });
        Assert.Equal(path, result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void AnUnexpectedAliasTargetIsNotRewritten()
    {
        Assert.Equal("/tmp/a", MacSystemPaths.Canonicalize("/tmp/a", _ => "/private/tmp/extra"));
        Assert.Equal("/var/a", MacSystemPaths.Canonicalize("/var/a", _ => null));
        Assert.Equal("/etc/a", MacSystemPaths.Canonicalize("/etc/a", _ => "/private/etc-other"));
    }

    [Fact]
    public void OtherPlatformsDoNotRewriteMacAliases()
    {
        if (OperatingSystem.IsMacOS()) return;
        Assert.Equal("/tmp/a", MacSystemPaths.Canonicalize("/tmp/a"));
        Assert.Equal("/var/folders/x", MacSystemPaths.Canonicalize("/var/folders/x"));
    }

    [Fact]
    public void FilesystemRootStaysRejected()
    {
        var root = Path.GetPathRoot(Directory.GetCurrentDirectory())!;
        var error = Assert.Throws<IOException>(() => LocalFileSystemProvider.ValidateMutationPath(root));
        Assert.Contains("root", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnUnexpectedIntermediateLinkIsStillRejected()
    {
        using var temp = new ScratchDirectory();
        var real = temp.Folder("real");
        var link = Path.Combine(temp.Root, "link");
        try { Directory.CreateSymbolicLink(link, real); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("This environment cannot create a directory symbolic link: " + ex.Message);
            return;
        }
        var provider = new LocalFileSystemProvider();
        var error = await Assert.ThrowsAsync<IOException>(() => provider.CreateDirectoryAsync(Path.Combine(link, "child")));
        Assert.Contains("symbolic links", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(real, "child")));
        try { Directory.Delete(link); } catch (IOException) { }
    }

    [Fact]
    public async Task ASymbolicLinkDestinationIsNotReplaced()
    {
        using var temp = new ScratchDirectory();
        var source = temp.File("source.txt", "new");
        var keep = temp.File("keep.txt", "keep");
        var link = Path.Combine(temp.Root, "link.txt");
        try { File.CreateSymbolicLink(link, keep); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("This environment cannot create a symbolic link: " + ex.Message);
            return;
        }
        var provider = new LocalFileSystemProvider();
        await Assert.ThrowsAsync<IOException>(() => provider.CopyFileAsync(source, link, true));
        Assert.Equal("keep", await File.ReadAllTextAsync(keep));
        Assert.True(File.Exists(source));
        try { File.Delete(link); } catch (IOException) { }
    }

    [Fact]
    public void VolumeCaseRulesStayIndependent()
    {
        Assert.False(MacVolumeCase.FromResult(1).Equals("Readme", "readme"));
        Assert.True(MacVolumeCase.FromResult(0).Equals("Readme", "readme"));
        Assert.True(MacVolumeCase.FromResult(null).Equals("Readme", "readme"));
        Assert.False(MacVolumeCase.IsReliable(null));
        Assert.True(MacVolumeCase.IsReliable(0));
        Assert.True(MacVolumeCase.IsReliable(1));
        Assert.Equal(StringComparison.Ordinal, MacVolumeCase.Comparison(1, 0));
        Assert.Equal(StringComparison.Ordinal, MacVolumeCase.Comparison(1, null));
        Assert.Equal(StringComparison.OrdinalIgnoreCase, MacVolumeCase.Comparison(0, 0));
        Assert.Equal(StringComparison.OrdinalIgnoreCase, MacVolumeCase.Comparison(null, null));
        Assert.Equal("/Volumes/Work", MacVolumeCase.SelectMount("/Volumes/Work/Docs/A", ["/", "/Volumes", "/Volumes/Work"]));
        Assert.Equal("/", MacVolumeCase.SelectMount("/private/tmp/a", ["/"]));
        Assert.Equal("/", MacVolumeCase.SelectMount("/VolumesExtra/a", ["/", "/Volumes"]));
    }

    [Fact]
    public void WindowsAndLinuxPathComparisonIsUnchanged()
    {
        var comparer = new LocalFileSystemProvider().PathComparer;
        if (OperatingSystem.IsWindows()) Assert.True(comparer.Equals(@"C:\A", @"c:\a"));
        else if (OperatingSystem.IsLinux()) Assert.False(comparer.Equals("/A", "/a"));
    }

    [Fact]
    public void MacLocationsSkipTheBootVolumeAndBlankEntries()
    {
        var locations = MacLocations.Build("/Users/me",
        [
            ("/Volumes/Macintosh HD", true),
            ("/Volumes/Work", false),
            ("/Volumes/Work", false),
            ("/", false),
            ("", false)
        ]);
        Assert.Equal(["Home", "Root", "Work"], locations.Select(item => item.DisplayName).ToArray());
        Assert.Equal("/Users/me", locations[0].RootPath);
        Assert.Equal("/", locations[1].RootPath);
        Assert.Equal("/Volumes/Work", locations[2].RootPath);
    }

    [Fact]
    public void WindowsSecretsStayDpapi()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI is the Windows credential store.");
        using var temp = new ScratchDirectory();
        var value = SecretProtector.Protect("hello", temp.Root);
        Assert.StartsWith("dpapi:", value, StringComparison.Ordinal);
        Assert.Equal("hello", SecretProtector.Unprotect(value, temp.Root));
        Assert.False(File.Exists(Path.Combine(temp.Root, "protector.key")));
    }

    [Fact]
    public async Task UnixCredentialFilesAreOwnerOnlyFromCreation()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix file modes.");
        if (OperatingSystem.IsWindows()) return;
        using var temp = new ScratchDirectory();
        var store = new SecretStore(Path.Combine(temp.Root, "connections.secrets.json"));
        await store.SaveAsync("prod", new("Password", "secret-value", "", "", true));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(temp.Root, "protector.key")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.Path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(temp.Root));
        File.SetUnixFileMode(Path.Combine(temp.Root, "protector.key"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        await store.SaveAsync("prod", new("Password", "secret-value", "", "", true));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(temp.Root, "protector.key")));
        Assert.Equal("secret-value", (await store.LoadAsync("prod"))!.Password);
    }

    [Fact]
    public void CredentialStorageDoesNotFollowASymbolicLink()
    {
        using var temp = new ScratchDirectory();
        var real = temp.Folder("real");
        var note = Path.Combine(real, "note.txt");
        File.WriteAllText(note, "untouched");
        var linked = Path.Combine(temp.Root, "linked");
        try { Directory.CreateSymbolicLink(linked, real); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("This environment cannot create a directory symbolic link: " + ex.Message);
            return;
        }
        var error = Assert.Throws<IOException>(() => SecretProtector.Protect("secret", linked));
        Assert.Contains("symbolic link", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("untouched", File.ReadAllText(note));
        Assert.False(File.Exists(Path.Combine(real, "protector.key")));
        try { Directory.Delete(linked); } catch (IOException) { }
    }

    [Fact]
    public async Task MacSystemTempAliasSupportsCreateCopyMoveRenameAndDelete()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires the macOS /tmp and /var aliases.");
        Assert.Equal("/private/tmp/child", MacSystemPaths.Canonicalize("/tmp/child"));
        Assert.Equal("/private/var/folders", MacSystemPaths.Canonicalize("/var/folders"));
        Assert.Equal("/private/etc/hosts", MacSystemPaths.Canonicalize("/etc/hosts"));
        var provider = new LocalFileSystemProvider();
        var operations = new FileOperationService();
        var root = "/tmp/dc-" + Guid.NewGuid().ToString("N");
        try
        {
            await provider.CreateDirectoryAsync(root);
            var source = Path.Combine(root, "a.txt");
            await using (var content = new MemoryStream("hello"u8.ToArray()))
                await provider.WriteFileAsync(source, content, false, null);
            var destination = Path.Combine(root, "copy.txt");
            await provider.CopyFileAsync(source, destination, false);
            var moved = Path.Combine(root, "moved.txt");
            await provider.MoveFileAsync(destination, moved, false);
            var renamed = await provider.RenameAsync(moved, "renamed.txt");
            Assert.Equal("hello", await File.ReadAllTextAsync(renamed));
            await provider.DeleteAsync(renamed, DeleteMode.Permanent);
            await provider.DeleteAsync(source, DeleteMode.Permanent);
            Assert.False(File.Exists(renamed));
            Assert.False(File.Exists(source));
            var entry = (await provider.GetEntryAsync(root))!;
            await operations.DeleteAsync(provider, [entry], DeleteMode.Permanent);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task HomeDirectoryCanBeCreatedInWhenItIsNotASymbolicLink()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS home directory.");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var probe = Path.Combine(home, ".dockcommander-probe-" + Guid.NewGuid().ToString("N"));
        var provider = new LocalFileSystemProvider();
        try
        {
            await provider.CreateDirectoryAsync(probe);
            Assert.True(Directory.Exists(probe));
        }
        finally
        {
            if (Directory.Exists(probe)) Directory.Delete(probe);
        }
    }

    [Fact]
    public async Task MacLocationsIncludeHomeAndDoNotRepeatTheRoot()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS locations.");
        var locations = await new LocalFileSystemProvider().GetLocationsAsync();
        Assert.Contains(locations, item => item.DisplayName == "Home" && Directory.Exists(item.RootPath));
        Assert.Equal(1, locations.Count(item => item.RootPath == "/"));
        Assert.Equal(locations.Count, locations.Select(item => item.RootPath).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void LiveVolumeComparisonFollowsThatVolume()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS volume case sensitivity.");
        var sensitive = MacVolumeCase.Sensitivity(Path.GetTempPath()) == 1;
        var provider = new LocalFileSystemProvider();
        var lower = Path.Combine(Path.GetTempPath(), "dc-case");
        var upper = Path.Combine(Path.GetTempPath(), "DC-CASE");
        Assert.Equal(!sensitive, provider.PathComparer.Equals(lower, upper));
        Assert.Equal(!sensitive, provider.ComparerFor(Path.GetTempPath()).Equals("A", "a"));
    }

    [Fact]
    public void AliasAndPrivateTempAreTheSameDirectory()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires the macOS /tmp alias.");
        var provider = new LocalFileSystemProvider();
        Assert.True(provider.IsSameOrDescendant("/tmp/dc/a.txt", "/private/tmp/dc"));
        Assert.True(provider.PathComparer.Equals("/tmp/dc/a.txt", "/private/tmp/dc/a.txt"));
    }
}
