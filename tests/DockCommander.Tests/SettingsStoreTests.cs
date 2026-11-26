using DockCommander.App;
using Xunit;

#pragma warning disable xUnit1051

namespace DockCommander.Tests;

public sealed class SettingsStoreTests
{
    private static readonly SemaphoreSlim DirectoryGate = new(1, 1);

    [Fact]
    public async Task SaveAcceptsAFileNameWithoutADirectory()
    {
        await DirectoryGate.WaitAsync();
        var previous = Directory.GetCurrentDirectory();
        var folder = Directory.CreateTempSubdirectory("dc-settings-");
        try
        {
            Directory.SetCurrentDirectory(folder.FullName);
            var store = new SettingsStore("settings.json");
            await store.SaveAsync(new("left-path", "right-path"));
            var loaded = await store.LoadAsync();
            Assert.Equal("left-path", loaded.LeftPath);
            Assert.Equal("right-path", loaded.RightPath);
            Assert.True(File.Exists(Path.Combine(folder.FullName, "settings.json")));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            folder.Delete(true);
            DirectoryGate.Release();
        }
    }
}
