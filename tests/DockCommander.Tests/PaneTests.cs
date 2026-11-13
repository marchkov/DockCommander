using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DockCommander.App;
using DockCommander.Core.Files;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(DockCommander.Tests.TestAppBuilder))]

namespace DockCommander.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<DockCommander.App.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class PaneTests
{
    [AvaloniaFact]
    public async Task LocationOverflowKeepsTargetsAndReturnsButtonsWhenWindowGrows()
    {
        using var temp = new ScratchDirectory();
        var pane = new FilePane(new LocalFileSystemProvider(), temp.Root);
        var window = new Window { Content = pane, Width = 340, Height = 400 };
        var locations = Enumerable.Range(0, 8).Select(i => new LocationEntry(i.ToString(),
            "Long volume label " + i, temp.Folder("disk" + i))).ToArray();
        window.Show();
        try
        {
            pane.SetLocations(locations);
            await pane.NavigateAsync(locations.Last().RootPath);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var more = pane.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MoreLocations");
            Assert.True(more.IsVisible);
            Assert.Contains("chosen", more.Classes);
            var menu = more.ContextMenu!;
            Assert.NotEmpty(menu.Items);
            string? requested = null;
            pane.NavigateRequested += (_, path) => requested = path;
            var last = menu.Items.OfType<MenuItem>().Last();
            Assert.Contains(locations.Last().RootPath, last.Header!.ToString());
            last.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(locations.Last().RootPath, requested);
            window.Width = 1800; window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.False(more.IsVisible);
            Assert.Equal(8, pane.GetVisualDescendants().OfType<Button>().Count(b => b.IsVisible && b != more));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DragSelectionUsesMarkedGroupOnlyWhenDraggingAMarkedRow()
    {
        using var temp = new ScratchDirectory();
        var first = temp.File("a.txt"); temp.File("b.txt"); var other = temp.File("c.md");
        var provider = new LocalFileSystemProvider();
        var pane = new FilePane(provider, temp.Root);
        var destination = new FilePane(provider, temp.Root);
        await pane.NavigateAsync(temp.Root);
        pane.MarkFiles("*.txt", true);
        var selected = pane.EntriesForDrag((await provider.GetEntryAsync(first))!);
        Assert.Equal(2, selected.Count);
        Assert.Equal(other, Assert.Single(pane.EntriesForDrag((await provider.GetEntryAsync(other))!)).FullPath);
        var payload = new FilePane.DraggedEntries(pane, selected);
        Assert.False(pane.CanAcceptDrop(payload, temp.Folder("target")));
        Assert.False(destination.CanAcceptDrop(payload, temp.Root));
        Assert.True(destination.CanAcceptDrop(payload, temp.Folder("target")));
        destination.IsEnabled = false;
        Assert.False(destination.CanAcceptDrop(payload, temp.Folder("target")));
    }

    [AvaloniaFact]
    public async Task MasksAndInversionPreserveCursorAndIgnoreFolders()
    {
        using var temp = new ScratchDirectory();
        temp.File("a.TXT"); temp.File("b.md"); temp.File("README"); temp.Folder("folder.txt");
        var pane = new FilePane(new LocalFileSystemProvider(), temp.Root);
        await pane.NavigateAsync(temp.Root);
        var current = pane.CurrentEntry;
        pane.MarkFiles("?.txt;*.md", true);
        Assert.Equal(new[] { "a.TXT", "b.md" }, pane.OperationEntries.Select(e => e.Name));
        pane.MarkFiles("*.TXT", false);
        Assert.Equal("b.md", Assert.Single(pane.OperationEntries).Name);
        pane.InvertFileMarks();
        Assert.Equal(new[] { "a.TXT", "README" }, pane.OperationEntries.Select(e => e.Name));
        pane.MarkFiles("*.*", true);
        Assert.Equal(3, pane.OperationEntries.Count);
        await pane.NavigateAsync(temp.Root);
        Assert.Equal(3, pane.OperationEntries.Count);
        Assert.Equal(current, pane.CurrentEntry);
        pane.MarkFiles("*", false);
        Assert.Empty(pane.OperationEntries); // Cursor is on the parent, which cannot be marked.
    }

    [AvaloniaFact]
    public async Task InsertMarksCurrentRowAndAdvancesWithoutLosingMarksOnRefresh()
    {
        using var temp = new ScratchDirectory();
        var first = temp.File("a.txt"); var second = temp.File("b.txt");
        var pane = new FilePane(new LocalFileSystemProvider(), temp.Root);
        var window = new Window { Content = pane, Width = 700, Height = 500 };
        window.Show();
        try
        {
            await pane.NavigateAsync(temp.Root);
            var list = pane.GetVisualDescendants().OfType<ListBox>().Single();
            list.SelectedIndex = 1; pane.FocusList(); Dispatcher.UIThread.RunJobs();
            window.KeyPress(Key.Insert, RawInputModifiers.None, PhysicalKey.Insert, null);
            window.KeyRelease(Key.Insert, RawInputModifiers.None, PhysicalKey.Insert, null);
            Assert.Equal(first, Assert.Single(pane.OperationEntries).FullPath);
            Assert.Equal(second, pane.CurrentEntry!.FullPath);
            await pane.NavigateAsync(temp.Root);
            Assert.Equal(first, Assert.Single(pane.OperationEntries).FullPath);
            Assert.Equal(second, pane.CurrentEntry!.FullPath);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ShiftRangeKeepsEarlierMarksAndShrinksWithoutThem()
    {
        using var temp = new ScratchDirectory();
        temp.File("a.txt"); temp.File("b.txt"); temp.File("c.txt"); temp.File("d.txt");
        var pane = new FilePane(new LocalFileSystemProvider(), temp.Root);
        var window = new Window { Content = pane, Width = 700, Height = 500 };
        window.Show();
        try
        {
            await pane.NavigateAsync(temp.Root);
            pane.MarkFiles("a.txt", true);
            var list = pane.GetVisualDescendants().OfType<ListBox>().Single();
            list.SelectedIndex = 0; pane.FocusList(); Dispatcher.UIThread.RunJobs();
            Press(window, Key.Down);
            Press(window, Key.Down);
            Press(window, Key.Down, RawInputModifiers.Shift);
            Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, pane.OperationEntries.Select(entry => entry.Name));
            Press(window, Key.Up, RawInputModifiers.Shift);
            Assert.Equal(new[] { "a.txt", "b.txt" }, pane.OperationEntries.Select(entry => entry.Name));
        }
        finally { window.Close(); }
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
    }

    [AvaloniaFact]
    public async Task FailedNavigationKeepsOldPathAndRows()
    {
        using var temp = new ScratchDirectory();
        var first = temp.File("a.txt");
        var pane = new FilePane(new LocalFileSystemProvider(), temp.Root);
        await pane.NavigateAsync(temp.Root, first);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => pane.NavigateAsync(Path.Combine(temp.Root, "missing")));
        Assert.Equal(temp.Root, pane.CurrentPath);
        Assert.Equal(first, pane.CurrentEntry!.FullPath);
    }

    [AvaloniaFact]
    public async Task ParentNavigationCanRestoreTheDirectoryCursor()
    {
        using var temp = new ScratchDirectory();
        var child = temp.Folder("child");
        var pane = new FilePane(new LocalFileSystemProvider(), temp.Root);
        await pane.NavigateAsync(child);
        await pane.NavigateAsync(temp.Root, child);
        Assert.Equal(child, pane.CurrentEntry!.FullPath);
    }

    [AvaloniaFact]
    public async Task EnterInPathFieldRequestsOnlyTheTypedPath()
    {
        using var temp = new ScratchDirectory();
        var child = temp.Folder("child");
        var pane = new FilePane(new LocalFileSystemProvider(), temp.Root);
        var window = new Window { Content = pane };
        string? requested = null;
        pane.NavigateRequested += (_, path) => requested = path;
        window.Show();
        try
        {
            await pane.NavigateAsync(temp.Root);
            var input = pane.GetVisualDescendants().OfType<TextBox>().Single();
            input.Text = child; input.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.Equal(child, requested);
        }
        finally { window.Close(); }
    }
}
