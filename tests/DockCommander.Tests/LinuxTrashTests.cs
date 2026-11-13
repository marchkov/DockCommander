using DockCommander.App.Platform;
using Xunit;

namespace DockCommander.Tests;

public sealed class LinuxTrashTests
{
    public static bool Enabled => OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("DC_TEST_TRASH") == "1";

    [Fact]
    public void GioUsesLiteralPathAndNeverPermanentDeletion()
    {
        var info = LinuxTrashService.CreateStartInfo("/tmp/--empty & $(literal).txt");
        Assert.False(info.UseShellExecute);
        Assert.Equal("/usr/bin/gio", info.FileName);
        Assert.Equal(new[] { "trash", "--", "/tmp/--empty & $(literal).txt" }, info.ArgumentList);
    }

    [Fact(Skip = "Set DC_TEST_TRASH=1 on Linux with isolated XDG_DATA_HOME.", SkipUnless = nameof(Enabled))]
    public async Task GioRecyclesFilesAndDirectoriesWithRecoveryMetadata()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Assert.False(string.IsNullOrWhiteSpace(dataHome));
        using var temp = new ScratchDirectory();
        var name = "dc-trash-" + Guid.NewGuid().ToString("N") + " space & тест.txt";
        var file = temp.File(name, "recover me");
        var folder = temp.Folder("dc-folder-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(Path.Combine(folder, "inside.txt"), "nested");
        var service = new LinuxTrashService();
        Assert.True(service.IsSupported);
        await service.RecycleAsync(file, TestContext.Current.CancellationToken);
        await service.RecycleAsync(folder, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(file)); Assert.False(Directory.Exists(folder));
        Assert.Equal("recover me", File.ReadAllText(Path.Combine(dataHome!, "Trash", "files", name)));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(dataHome!, "Trash", "files", Path.GetFileName(folder), "inside.txt")));
        var info = File.ReadAllText(Path.Combine(dataHome!, "Trash", "info", name + ".trashinfo"));
        Assert.Contains("[Trash Info]", info); Assert.Contains("DeletionDate=", info);
        Assert.Contains(file, Uri.UnescapeDataString(info));
        var cancelled = temp.File("cancelled.txt");
        using var token = new CancellationTokenSource(); token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RecycleAsync(cancelled, token.Token));
        Assert.True(File.Exists(cancelled));
        await Assert.ThrowsAsync<IOException>(() => service.RecycleAsync(Path.Combine(temp.Root, "missing"), TestContext.Current.CancellationToken));
    }
}
