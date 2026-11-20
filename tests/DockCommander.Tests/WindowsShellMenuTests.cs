using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DockCommander.App;
using DockCommander.App.Platform;
using DockCommander.Core.Files;
using Xunit;

namespace DockCommander.Tests;

public sealed class WindowsShellMenuTests
{
    [Fact]
    public async Task NativeMenusCanBeCreatedForUnicodeFilesMultipleSelectionAndBackground()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows Shell only."); return; }
        using var temp = new ScratchDirectory();
        var first = temp.File("папка/первый.txt", "one");
        var second = temp.File("папка/second.txt", "two");
        var directory = Path.GetDirectoryName(first)!;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                Assert.True(WindowsShellMenu.CountItems(directory, [first]) > 0);
                Assert.True(WindowsShellMenu.CountItems(directory, [first, second]) > 0);
                Assert.True(WindowsShellMenu.CountItems(directory, []) > 0);
                done.SetResult();
            }
            catch (Exception error) { done.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal("one", File.ReadAllText(first));
        Assert.Equal("two", File.ReadAllText(second));
    }

    [AvaloniaTheory]
    [InlineData("marked")]
    [InlineData("unmarked")]
    [InlineData("background")]
    [InlineData("parent")]
    public async Task ShiftRightClickUsesSystemMenuOnlyOnWindowsAndPreservesSelectionRules(string target)
    {
        using var temp = new ScratchDirectory();
        var first = temp.File("files/a.txt");
        var second = temp.File("files/b.txt");
        var other = temp.File("files/c.md");
        var pane = new FilePane(new LocalFileSystemProvider(), Path.GetDirectoryName(first)!);
        var window = new Window { Width = 900, Height = 650, Content = pane };
        IReadOnlyList<string>? requested = null;
        pane.SystemMenuRequested += (_, paths, _) => requested = paths;
        window.Show();
        try
        {
            await pane.NavigateAsync(pane.CurrentPath);
            pane.MarkFiles("*.txt", true);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var list = pane.GetVisualDescendants().OfType<ListBox>().Single();
            Point point;
            if (target == "background")
                point = list.TranslatePoint(new Point(30, list.Bounds.Height - 6), window)!.Value;
            else
            {
                var row = list.GetVisualDescendants().OfType<ListBoxItem>().Single(item =>
                    item.DataContext is FileRow r && (target == "parent" ? r.Entry.Name == ".." :
                        r.Entry.FullPath == (target == "marked" ? first : other)));
                point = row.TranslatePoint(new Point(40, 10), window)!.Value;
            }
            window.MouseDown(point, MouseButton.Right, RawInputModifiers.Shift);
            window.MouseUp(point, MouseButton.Right, RawInputModifiers.Shift);
            Dispatcher.UIThread.RunJobs();
            if (OperatingSystem.IsWindows())
            {
                Assert.False(list.ContextMenu!.IsOpen);
                if (target == "parent") Assert.Null(requested);
                else Assert.Equal(target == "background" ? [] : target == "marked" ? new[] { first, second } : [other], requested);
            }
            else
            {
                Assert.Null(requested);
                Assert.True(list.ContextMenu!.IsOpen);
            }
            list.ContextMenu!.Close();
            // A following ordinary click must still open DockCommander's own menu.
            window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.True(list.ContextMenu.IsOpen);
            list.ContextMenu.Close();
        }
        finally { window.Close(); }
    }
}
