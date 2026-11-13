using System.Text;
using DockCommander.App.Platform;
using DockCommander.Core.Files;
using DockCommander.Core.Operations;
using Xunit;

namespace DockCommander.Tests;

public sealed class FileAccessTests
{
    private readonly LocalFileSystemProvider _provider = new();
    private readonly FileOperationService _operations = new();
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0, "UTF-8")]
    [InlineData(1, "UTF-8")]
    [InlineData(2, "UTF-16 LE")]
    [InlineData(3, "UTF-16 BE")]
    [InlineData(4, "UTF-32 LE")]
    [InlineData(5, "UTF-32 BE")]
    public async Task ViewerReadsUnicodeWithoutChangingTheFile(int kind, string expectedName)
    {
        using var temp = new ScratchDirectory();
        const string text = "Hello, мир!\r\nÁrvíztűrő tükörfúrógép 😀\n\tEnd.";
        Encoding encoding = kind switch
        {
            0 => new UTF8Encoding(false), 1 => new UTF8Encoding(true),
            2 => new UnicodeEncoding(false, true), 3 => new UnicodeEncoding(true, true),
            4 => new UTF32Encoding(false, true), _ => new UTF32Encoding(true, true)
        };
        var path = temp.File("unicode.txt");
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        await File.WriteAllBytesAsync(path, bytes, Token);
        var preview = await new TextPreviewService().ReadAsync(_provider, path, Token);
        Assert.Equal(text, preview.Text);
        Assert.Equal(expectedName, preview.EncodingName);
        Assert.False(preview.IsTruncated);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Token));
    }

    [Fact]
    public async Task ViewerHandlesAnEmptyFile()
    {
        using var temp = new ScratchDirectory();
        var path = temp.File("empty.txt", "");
        var preview = await new TextPreviewService().ReadAsync(_provider, path, Token);
        Assert.Empty(preview.Text);
        Assert.False(preview.IsTruncated);
    }

    [Fact]
    public async Task ViewerTruncatesAtLimitWithoutBreakingUnicodeCharacter()
    {
        using var temp = new ScratchDirectory();
        var path = temp.File("large.txt", new string('a', TextPreviewService.MaxBytes - 1) + "😀more");
        var preview = await new TextPreviewService().ReadAsync(_provider, path, Token);
        Assert.True(preview.IsTruncated);
        Assert.Equal(new string('a', TextPreviewService.MaxBytes - 1), preview.Text);
    }

    [Fact]
    public async Task ExactLimitIsNotReportedAsTruncated()
    {
        using var temp = new ScratchDirectory();
        var path = temp.File("exact.txt", new string('a', TextPreviewService.MaxBytes));
        var preview = await new TextPreviewService().ReadAsync(_provider, path, Token);
        Assert.False(preview.IsTruncated);
        Assert.Equal(TextPreviewService.MaxBytes, preview.Text.Length);
    }

    [Theory]
    [InlineData(new byte[] { 0, 1, 2, 3 })]
    [InlineData(new byte[] { 0xFF, 0xFF })]
    [InlineData(new byte[] { 0xE2, 0x82 })]
    public async Task BinaryAndMalformedTextAreRejected(byte[] bytes)
    {
        using var temp = new ScratchDirectory();
        var path = temp.File("binary.dat");
        await File.WriteAllBytesAsync(path, bytes, Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => new TextPreviewService().ReadAsync(_provider, path, Token));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Token));
    }

    [Fact]
    public async Task ViewerHonorsCancellation()
    {
        using var temp = new ScratchDirectory();
        var path = temp.File("file.txt");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TextPreviewService().ReadAsync(_provider, path, cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameChangesOnlyTheNameAndPreservesContents(bool directory)
    {
        using var temp = new ScratchDirectory();
        var source = directory ? temp.Folder("before") : temp.File("before");
        if (directory) temp.File("before/child.txt", "keep");
        var entry = (await _provider.GetEntryAsync(source, Token))!;
        var destination = await _operations.RenameAsync(_provider, entry, "after", Token);
        Assert.Equal(Path.Combine(temp.Root, "after"), destination);
        Assert.False(File.Exists(source) || Directory.Exists(source));
        Assert.Equal(directory ? "keep" : "source", await File.ReadAllTextAsync(directory ? Path.Combine(destination, "child.txt") : destination, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameNeverReplacesAnExistingEntry(bool directory)
    {
        using var temp = new ScratchDirectory();
        var source = directory ? temp.Folder("before") : temp.File("before", "source");
        if (directory) { temp.File("before/a.txt", "source"); temp.File("after/b.txt", "keep"); }
        else temp.File("after", "keep");
        var entry = (await _provider.GetEntryAsync(source, Token))!;
        await Assert.ThrowsAsync<IOException>(() => _operations.RenameAsync(_provider, entry, "after", Token));
        Assert.Equal("source", await File.ReadAllTextAsync(directory ? Path.Combine(source, "a.txt") : source, Token));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(temp.Root, directory ? "after/b.txt" : "after"), Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameSupportsChangingOnlyLetterCase(bool directory)
    {
        using var temp = new ScratchDirectory();
        var source = directory ? temp.Folder("before") : temp.File("before");
        var entry = (await _provider.GetEntryAsync(source, Token))!;
        await _operations.RenameAsync(_provider, entry, "BEFORE", Token);
        Assert.Contains((await _provider.ListAsync(temp.Root, Token)), x => x.Name == "BEFORE");
        Assert.DoesNotContain((await _provider.ListAsync(temp.Root, Token)), x => x.Name == "before");
    }

    [Fact]
    public async Task RenameRejectsTraversalAndParentEntry()
    {
        using var temp = new ScratchDirectory();
        var source = temp.File("keep.txt");
        var entry = (await _provider.GetEntryAsync(source, Token))!;
        await Assert.ThrowsAsync<ArgumentException>(() => _operations.RenameAsync(_provider, entry, "../outside.txt", Token));
        var parent = (await _provider.ListAsync(temp.Root, Token)).First();
        await Assert.ThrowsAsync<IOException>(() => _operations.RenameAsync(_provider, parent, "bad", Token));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void EditorPassesPathsWithSpacesAndShellCharactersAsOneArgument()
    {
        using var temp = new ScratchDirectory();
        var path = temp.File("notes & $(literal).txt");
        var executable = Path.Combine(temp.Root, "My Editor", "editor.exe");
        var info = ExternalFileLauncher.CreateEditInfo(path, executable);
        Assert.False(info.UseShellExecute);
        Assert.Equal(executable, info.FileName);
        Assert.Equal(path, Assert.Single(info.ArgumentList));
        Assert.Empty(info.Arguments);
    }

    [Fact]
    public void OpenUsesTheFileAssociationWithoutCommandArguments()
    {
        using var temp = new ScratchDirectory();
        var path = temp.File("space name.txt");
        var info = ExternalFileLauncher.CreateOpenInfo(path);
        Assert.True(info.UseShellExecute);
        Assert.Equal(path, info.FileName);
        Assert.Empty(info.ArgumentList);
    }

    [Fact]
    public void WindowsDefaultEditorIsNotepadRatherThanTheFileAssociation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new ScratchDirectory();
        var path = temp.File("script.cmd", "echo should-not-run");
        var info = ExternalFileLauncher.CreateEditInfo(path, "");
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"), info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal(path, Assert.Single(info.ArgumentList));
    }

    [Fact]
    public void LauncherRejectsDirectoriesAndMissingFiles()
    {
        using var temp = new ScratchDirectory();
        Assert.Throws<IOException>(() => ExternalFileLauncher.CreateOpenInfo(temp.Root));
        Assert.Throws<FileNotFoundException>(() => ExternalFileLauncher.CreateEditInfo(Path.Combine(temp.Root, "missing.txt"), "editor"));
    }
}
