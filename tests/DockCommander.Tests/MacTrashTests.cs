using DockCommander.App.Platform;
using DockCommander.Core.Files;
using Xunit;

namespace DockCommander.Tests;

public sealed class MacTrashTests
{
    [Fact]
    public async Task NativeTrashMovesAFileAndADirectoryWithoutPermanentDeletion()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "NSFileManager Trash is macOS-only.");
        using var temp = new ScratchDirectory();
        var name = "dc-trash-" + Guid.NewGuid().ToString("N") + " space.txt";
        var file = temp.File(name, "recover me");
        var folder = temp.Folder("dc-folder-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(Path.Combine(folder, "inside.txt"), "nested", TestContext.Current.CancellationToken);
        var service = new MacTrashService();
        Assert.True(service.IsSupported);
        LocalFileSystemProvider.ValidateMutationPath(file);
        var trashedFile = MacTrashService.Native.Trash(file);
        var trashedFolder = MacTrashService.Native.Trash(folder);
        try
        {
            Assert.False(File.Exists(file));
            Assert.False(Directory.Exists(folder));
            Assert.NotEqual(file, trashedFile);
            Assert.Equal("recover me", await File.ReadAllTextAsync(trashedFile, TestContext.Current.CancellationToken));
            Assert.Equal("nested", await File.ReadAllTextAsync(Path.Combine(trashedFolder, "inside.txt"), TestContext.Current.CancellationToken));
            var cancelled = temp.File("cancelled.txt", "stay");
            using var token = new CancellationTokenSource();
            token.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RecycleAsync(cancelled, token.Token));
            Assert.True(File.Exists(cancelled));
            await Assert.ThrowsAsync<IOException>(() => service.RecycleAsync(Path.Combine(temp.Root, "missing"), TestContext.Current.CancellationToken));
        }
        finally
        {
            if (File.Exists(trashedFile)) File.Delete(trashedFile);
            if (Directory.Exists(trashedFolder)) Directory.Delete(trashedFolder, true);
        }
    }

    [Fact]
    public void OtherPlatformsDoNotReportMacTrash()
    {
        if (OperatingSystem.IsMacOS()) return;
        Assert.False(new MacTrashService().IsSupported);
    }
}
