using DockCommander.App.Platform;
using Xunit;

namespace DockCommander.Tests;

public sealed class WindowsRecycleTests
{
    public static bool Enabled => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("DC_TEST_RECYCLE") == "1";

    [Fact(Skip = "Set DC_TEST_RECYCLE=1 on Windows to run the real recycle-bin test.", SkipUnless = nameof(Enabled))]
    [Trait("Category", "WindowsIntegration")]
    public async Task WindowsShellRecyclesAnIsolatedTestFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new ScratchDirectory();
        var path = temp.File("DockCommander-recycle-smoke.txt", "Disposable DockCommander integration-test file.");
        await new WindowsTrashService().RecycleAsync(path, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(path));
    }
}
