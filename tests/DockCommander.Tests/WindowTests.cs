using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Styling;
using DockCommander.App;
using DockCommander.Core.Files;
using DockCommander.App.Platform;
using Xunit;

namespace DockCommander.Tests;

public sealed class WindowTests
{
    [AvaloniaFact]
    public async Task ExternalDropCopiesOnceAfterConfirmationAndKeepsOriginals()
    {
        using var temp = new ScratchDirectory();
        var folder = temp.Folder("external/tree"); var child = temp.File("external/tree/a.txt", "external");
        var (window, left, right, _) = await Open(temp);
        try
        {
            right.RequestExternalDrop([folder, child, folder], right.CurrentPath);
            await WaitUntil(() => window.OwnedWindows.Any());
            Assert.Equal("Copy", window.OwnedWindows.Single().Title);
            Assert.False(Directory.Exists(Path.Combine(right.CurrentPath, "tree")));
            Press(window.OwnedWindows.Single(), Key.Escape);
            await WaitUntil(() => right.IsEnabled);
            Assert.False(Directory.Exists(Path.Combine(right.CurrentPath, "tree")));
            right.RequestExternalDrop([folder, child, folder], right.CurrentPath);
            await WaitUntil(() => window.OwnedWindows.Any());
            Press(window.OwnedWindows.Single(), Key.Enter);
            await WaitUntil(() => right.IsEnabled);
            Assert.Equal("external", File.ReadAllText(Path.Combine(right.CurrentPath, "tree/a.txt")));
            Assert.True(File.Exists(child));
            Assert.False(File.Exists(Path.Combine(right.CurrentPath, "a.txt")));
            Assert.Empty(Directory.GetFileSystemEntries(left.CurrentPath));
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task ExternalDropRejectsMissingItemsAndSelfCopyBeforeChangingFiles()
    {
        using var temp = new ScratchDirectory();
        var file = temp.File("left/a.txt", "keep");
        var (window, left, _, _) = await Open(temp);
        try
        {
            left.RequestExternalDrop([file], left.CurrentPath);
            await WaitUntil(() => left.IsEnabled);
            Assert.Empty(window.OwnedWindows);
            left.RequestExternalDrop([Path.Combine(temp.Root, "missing")], left.CurrentPath);
            await WaitUntil(() => left.IsEnabled);
            Assert.Empty(window.OwnedWindows);
            Assert.Equal("keep", File.ReadAllText(file));
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task DroppedFilesUseConfirmationAndCanMoveIntoTargetFolder()
    {
        using var temp = new ScratchDirectory();
        var source = temp.File("left/a.txt");
        var target = temp.Folder("right/inside");
        var (window, left, right, _) = await Open(temp);
        try
        {
            var payload = new FilePane.DraggedEntries(left, [(await left.Provider.GetEntryAsync(source))!]);
            right.RequestDrop(payload, target, true);
            await WaitUntil(() => window.OwnedWindows.Any());
            Assert.Equal("Move", window.OwnedWindows.Single().Title);
            Assert.True(File.Exists(source));
            Press(window.OwnedWindows.Single(), Key.Escape);
            await WaitUntil(() => left.IsEnabled);
            Assert.True(File.Exists(source)); Assert.Empty(Directory.GetFiles(target));
            right.RequestDrop(payload, target, true);
            await WaitUntil(() => window.OwnedWindows.Any());
            Press(window.OwnedWindows.Single(), Key.Enter);
            await WaitUntil(() => left.IsEnabled);
            Assert.False(File.Exists(source)); Assert.True(File.Exists(Path.Combine(target, "a.txt")));
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task RightClickSelectsClickedFileAndMenuCopiesIt()
    {
        using var temp = new ScratchDirectory();
        temp.File("left/a.txt"); var other = temp.File("left/b.md");
        var (window, left, right, _) = await Open(temp);
        try
        {
            left.MarkFiles("*.txt", true);
            var list = left.GetVisualDescendants().OfType<ListBox>().Single();
            var row = list.GetVisualDescendants().OfType<ListBoxItem>()
                .Single(item => item.DataContext is FileRow r && r.Entry.FullPath == other);
            var point = row.TranslatePoint(new Avalonia.Point(40, 10), window)!.Value;
            window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(other, left.CurrentEntry!.FullPath);
            Assert.Equal(other, Assert.Single(left.OperationEntries).FullPath);
            var menu = list.ContextMenu!;
            Assert.True(menu.Items.OfType<MenuItem>().Single(item => item.Name == "Context_copy").IsEnabled);
            menu.Close();
            menu.Items.OfType<MenuItem>().Single(item => item.Name == "Context_copy")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntil(() => window.OwnedWindows.Any());
            Press(window.OwnedWindows.Single(), Key.Enter);
            await WaitUntil(() => left.IsEnabled);
            Assert.True(File.Exists(Path.Combine(right.CurrentPath, "b.md")));
            Assert.False(File.Exists(Path.Combine(right.CurrentPath, "a.txt")));
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task RightClickOnEmptySpaceKeepsMarksAndLimitsTheMenu()
    {
        using var temp = new ScratchDirectory();
        var file = temp.File("left/a.txt", "keep");
        var (window, left, _, _) = await Open(temp);
        try
        {
            left.MarkFiles("*.txt", true);
            var cursor = left.CurrentEntry;
            var list = left.GetVisualDescendants().OfType<ListBox>().Single();
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Point? empty = null;
            for (var y = list.Bounds.Height - 4; y >= 4; y -= 10)
            {
                var local = new Avalonia.Point(30, y);
                var hit = list.InputHitTest(local) as Visual;
                var row = hit as ListBoxItem ?? hit?.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
                if (hit is not null && row is null) { empty = list.TranslatePoint(local, window); break; }
            }
            Assert.True(empty is not null);
            window.MouseDown(empty!.Value, MouseButton.Right); window.MouseUp(empty.Value, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(file, Assert.Single(left.OperationEntries).FullPath);
            Assert.Equal(cursor, left.CurrentEntry);
            MenuItem Item(string name) => list.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == name);
            Assert.False(Item("Context_copy").IsEnabled);
            Assert.False(Item("Context_recycle").IsEnabled);
            Assert.True(Item("Context_mkdir").IsEnabled);
            Assert.True(Item("Context_refresh").IsEnabled);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task NumpadSelectionAcceptsMaskAndRestoresListFocus()
    {
        using var temp = new ScratchDirectory();
        temp.File("left/a.txt"); temp.File("left/b.md");
        var (window, left, _, _) = await Open(temp);
        try
        {
            Press(window, Key.Add);
            await WaitUntil(() => window.OwnedWindows.Any());
            window.OwnedWindows.Single().KeyTextInput("*.txt");
            Press(window.OwnedWindows.Single(), Key.Enter);
            await WaitUntil(() => left.IsEnabled);
            Assert.Equal("a.txt", Assert.Single(left.OperationEntries).Name);
            Press(window, Key.Multiply);
            await WaitUntil(() => left.IsEnabled);
            Assert.Equal("b.md", Assert.Single(left.OperationEntries).Name);
            Press(window, Key.Subtract);
            await WaitUntil(() => window.OwnedWindows.Any());
            Press(window.OwnedWindows.Single(), Key.Enter);
            await WaitUntil(() => left.IsEnabled);
            Assert.Empty(left.OperationEntries);
            Assert.IsType<ListBoxItem>(window.FocusManager!.GetFocusedElement());
            Press(window, Key.Add);
            await WaitUntil(() => window.OwnedWindows.Any());
            Press(window.OwnedWindows.Single(), Key.Escape);
            await WaitUntil(() => left.IsEnabled);
            Assert.Empty(left.OperationEntries);
            left.FocusPath(); Press(window, Key.Add);
            Assert.Empty(window.OwnedWindows);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task AltLocationShortcutsTargetTheirPaneAndEscapeKeepsPaths()
    {
        using var temp = new ScratchDirectory();
        var (window, left, right, _) = await Open(temp);
        var leftPath = left.CurrentPath; var rightPath = right.CurrentPath;
        try
        {
            Press(window, Key.F2, RawInputModifiers.Alt);
            await WaitUntil(() => window.OwnedWindows.Any());
            Assert.Equal("Right panel location", window.OwnedWindows.Single().Title);
            Assert.IsType<ListBox>(window.OwnedWindows.Single().FocusManager!.GetFocusedElement());
            Press(window.OwnedWindows.Single(), Key.Enter);
            await WaitUntil(() => right.IsEnabled);
            Assert.Equal(Path.GetPathRoot(rightPath), right.CurrentPath);
            Assert.Equal(leftPath, left.CurrentPath);
            Press(window, Key.F1, RawInputModifiers.Alt);
            await WaitUntil(() => window.OwnedWindows.Any());
            Assert.Equal("Left panel location", window.OwnedWindows.Single().Title);
            Press(window.OwnedWindows.Single(), Key.Escape);
            await WaitUntil(() => right.IsEnabled);
            Assert.Equal(leftPath, left.CurrentPath);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task DialogCloseCancelsAndConflictEnterOverwrites()
    {
        using var temp = new ScratchDirectory();
        var source = temp.File("left/a.txt", "new");
        var destination = temp.File("right/a.txt", "old");
        var (window, left, _, _) = await Open(temp);
        try
        {
            await left.NavigateAsync(left.CurrentPath, source); left.FocusList();
            Press(window, Key.F5);
            await WaitUntil(() => window.OwnedWindows.Any());
            var dialog = window.OwnedWindows.Single();
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DialogCloseButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => left.IsEnabled);
            Assert.Equal("old", File.ReadAllText(destination));
            Press(window, Key.F5);
            await WaitUntil(() => window.OwnedWindows.Any());
            Press(window.OwnedWindows.Single(), Key.Enter);
            await WaitUntil(() => window.OwnedWindows.Any(w => w.Title == "Destination exists"));
            var conflict = window.OwnedWindows.Single();
            Assert.Equal(WindowDecorations.None, conflict.WindowDecorations);
            Assert.Equal("Overwrite", Assert.IsType<Button>(conflict.FocusManager!.GetFocusedElement()).Content);
            Press(conflict, Key.Enter);
            await WaitUntil(() => left.IsEnabled);
            Assert.Equal("new", File.ReadAllText(destination));
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task CaptionButtonsChangeStateAndCloseThroughSettingsSave()
    {
        using var temp = new ScratchDirectory();
        var (window, _, _, store) = await Open(temp);
        try
        {
            Button Find(string name) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
            void Invoke(string name) => Find(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(WindowDecorations.None, window.WindowDecorations);
            Invoke("MaximizeButton");
            Assert.Equal(WindowState.Maximized, window.WindowState);
            Assert.Equal("Restore", Avalonia.Automation.AutomationProperties.GetName(Find("MaximizeButton")));
            Invoke("MaximizeButton");
            Assert.Equal(WindowState.Normal, window.WindowState);
            Invoke("MinimizeButton");
            Assert.Equal(WindowState.Minimized, window.WindowState);
            window.WindowState = WindowState.Normal;
            Invoke("DarkThemeButton");
            Invoke("CloseButton");
            await WaitUntil(() => !window.IsVisible);
            Assert.Equal("Dark", (await store.LoadAsync()).Theme);
        }
        finally { if (window.IsVisible) await Close(window); }
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        if (window.IsVisible) window.KeyRelease(key, modifiers, PhysicalKey.None, null);
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        for (var i = 0; i < 200; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (predicate()) return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(predicate(), "The UI did not reach the expected state.");
    }

    private static async Task<(MainWindow Window, FilePane Left, FilePane Right, SettingsStore Settings)> Open(ScratchDirectory temp, ITrashService? trash = null, IExternalFileLauncher? launcher = null)
    {
        var left = temp.Folder("left"); var right = temp.Folder("right");
        var store = new SettingsStore(Path.Combine(temp.Root, "settings.json"));
        await store.SaveAsync(new(left, right));
        var window = new MainWindow(store, new LocalFileSystemProvider(trash), launcher);
        window.Show();
        var panes = window.GetVisualDescendants().OfType<FilePane>().ToArray();
        await WaitUntil(() => panes.All(p => p.IsEnabled && p.CurrentPath.StartsWith(temp.Root, StringComparison.Ordinal)));
        return (window, panes[0], panes[1], store);
    }

    private static async Task Close(MainWindow window)
    {
        foreach (var child in window.OwnedWindows.ToArray()) child.Close();
        await WaitUntil(() => window.GetVisualDescendants().OfType<FilePane>().All(p => p.IsEnabled));
        window.Close();
        await WaitUntil(() => !window.IsVisible);
    }

    private static void Click(Window dialog, string caption)
    {
        var button = dialog.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, caption));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    [AvaloniaFact]
    public async Task F7CreatesDirectoryAndReturnsFocusToList()
    {
        using var temp = new ScratchDirectory();
        var (window, left, _, _) = await Open(temp);
        try
        {
            Press(window, Key.F7);
            await WaitUntil(() => window.OwnedWindows.Count() == 1);
            var dialog = window.OwnedWindows.Single();
            dialog.KeyTextInput("created");
            Press(dialog, Key.Enter);
            await WaitUntil(() => left.IsEnabled && Directory.Exists(Path.Combine(left.CurrentPath, "created")));
            Assert.Equal("created", left.CurrentEntry!.Name);
            Assert.IsType<ListBoxItem>(window.FocusManager!.GetFocusedElement());
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task F5CopiesToOppositePaneAndEscapeCancelsConfirmation()
    {
        using var temp = new ScratchDirectory();
        var source = temp.File("left/a.txt");
        var (window, left, right, _) = await Open(temp);
        try
        {
            await left.NavigateAsync(left.CurrentPath, source); left.FocusList(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(source, left.CurrentEntry?.FullPath);
            Press(window, Key.F5);
            await WaitUntil(() => window.OwnedWindows.Any());
            Press(window.OwnedWindows.Single(), Key.Escape);
            await WaitUntil(() => left.IsEnabled);
            Assert.False(File.Exists(Path.Combine(right.CurrentPath, "a.txt")));
            Press(window, Key.F5);
            await WaitUntil(() => window.OwnedWindows.Any());
            Assert.Equal(WindowDecorations.None, window.OwnedWindows.Single().WindowDecorations);
            Assert.Equal("Copy", Assert.IsType<Button>(window.OwnedWindows.Single().FocusManager!.GetFocusedElement()).Content);
            Press(window.OwnedWindows.Single(), Key.Enter);
            await WaitUntil(() => left.IsEnabled);
            Assert.Equal("source", File.ReadAllText(Path.Combine(right.CurrentPath, "a.txt")));
            Assert.Equal(source, left.CurrentEntry!.FullPath);
            Assert.IsType<ListBoxItem>(window.FocusManager!.GetFocusedElement());
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task F8UsesRecycleAndShiftF8PermanentlyDeletes()
    {
        using var temp = new ScratchDirectory();
        var source = temp.File("left/a.txt");
        var trash = new RecordingTrash();
        var (window, left, _, _) = await Open(temp, trash);
        try
        {
            await left.NavigateAsync(left.CurrentPath, source); left.FocusList(); Dispatcher.UIThread.RunJobs();
            Press(window, Key.F8);
            await WaitUntil(() => window.OwnedWindows.Any());
            Assert.Equal("Move to recycle bin", window.OwnedWindows.Single().Title);
            Press(window.OwnedWindows.Single(), Key.Enter);
            await WaitUntil(() => left.IsEnabled);
            Assert.Equal(source, Assert.Single(trash.Paths));
            Assert.True(File.Exists(source));
            Press(window, Key.F8, RawInputModifiers.Shift);
            await WaitUntil(() => window.OwnedWindows.Any());
            Assert.Equal("Delete permanently", window.OwnedWindows.Single().Title);
            Press(window.OwnedWindows.Single(), Key.Enter);
            await WaitUntil(() => left.IsEnabled);
            Assert.False(File.Exists(source));
            Assert.Single(trash.Paths);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task PermanentDeleteConfirmationListsMarkedNames()
    {
        using var temp = new ScratchDirectory();
        var first = temp.File("left/a.txt", "keep");
        temp.File("left/b.txt");
        var (window, left, _, _) = await Open(temp);
        try
        {
            left.MarkFiles("*.txt", true);
            left.FocusList(); Dispatcher.UIThread.RunJobs();
            Press(window, Key.F8, RawInputModifiers.Shift);
            await WaitUntil(() => window.OwnedWindows.Any());
            var text = string.Join("\n", window.OwnedWindows.Single().GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text));
            Assert.Contains("a.txt", text);
            Assert.Contains("b.txt", text);
            Press(window.OwnedWindows.Single(), Key.Escape);
            await WaitUntil(() => left.IsEnabled);
            Assert.True(File.Exists(first));
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task PathEnterDoesNotAlsoOpenTheSelectedDirectoryAndTabPersistsActivePane()
    {
        using var temp = new ScratchDirectory();
        var child = temp.Folder("left/child");
        temp.Folder("left/child/nested");
        var (window, left, right, settings) = await Open(temp);
        try
        {
            var path = left.GetVisualDescendants().OfType<TextBox>().Single();
            path.Text = child; path.Focus();
            Press(window, Key.Enter);
            await WaitUntil(() => left.IsEnabled && left.CurrentPath == child);
            Assert.Equal(Path.Combine(temp.Root, "right"), right.CurrentPath);
            Press(window, Key.Tab);
        }
        finally { await Close(window); }
        var restored = await settings.LoadAsync();
        Assert.Equal(child, restored.LeftPath);
        Assert.False(restored.LeftActive);
    }

    [AvaloniaFact]
    public async Task ThemeSwitchPreservesSelectionPersistsAndThemesDialogs()
    {
        using var temp = new ScratchDirectory();
        var source = temp.File("left/a.txt");
        temp.File("left/b.txt");
        var (window, left, _, settings) = await Open(temp);
        try
        {
            await left.NavigateAsync(left.CurrentPath, source); left.FocusList(); Dispatcher.UIThread.RunJobs();
            Press(window, Key.Insert);
            var cursor = left.CurrentEntry;
            var lightBackground = window.Background;
            var dark = window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "DarkThemeButton");
            dark.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            Assert.NotEqual(lightBackground, window.Background);
            Assert.Equal(source, Assert.Single(left.OperationEntries).FullPath);
            Assert.Equal(cursor, left.CurrentEntry);
            Press(window, Key.F7);
            await WaitUntil(() => window.OwnedWindows.Any());
            Assert.Equal(ThemeVariant.Dark, window.OwnedWindows.Single().ActualThemeVariant);
            Press(window.OwnedWindows.Single(), Key.Escape);
            await WaitUntil(() => left.IsEnabled);
            Press(window, Key.T, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);
            Press(window, Key.T, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
        }
        finally { await Close(window); }
        Assert.Equal("Dark", (await settings.LoadAsync()).Theme);
        var reopened = new MainWindow(settings, new LocalFileSystemProvider());
        reopened.Show();
        try
        {
            await WaitUntil(() => reopened.GetVisualDescendants().OfType<FilePane>().All(p => p.IsEnabled));
            Assert.Equal(ThemeVariant.Dark, reopened.ActualThemeVariant);
        }
        finally { await Close(reopened); }
    }

    [AvaloniaFact]
    public async Task EnterAndF4UseTheCurrentFileRatherThanMarkedFiles()
    {
        using var temp = new ScratchDirectory();
        var first = temp.File("left/a.txt"); var second = temp.File("left/b.txt");
        var launcher = new RecordingLauncher();
        var (window, left, _, _) = await Open(temp, launcher: launcher);
        try
        {
            await left.NavigateAsync(left.CurrentPath, first); left.FocusList(); Dispatcher.UIThread.RunJobs();
            Press(window, Key.Insert);
            Assert.Equal(second, left.CurrentEntry!.FullPath);
            Press(window, Key.Enter);
            await WaitUntil(() => left.IsEnabled);
            Assert.Equal(second, Assert.Single(launcher.Opened));
            Press(window, Key.F4);
            await WaitUntil(() => left.IsEnabled);
            Assert.Equal(second, Assert.Single(launcher.Edited).Path);
            Assert.Equal(first, Assert.Single(left.OperationEntries).FullPath);
            Assert.IsType<ListBoxItem>(window.FocusManager!.GetFocusedElement());
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task F3ShowsReadOnlyTextInCurrentThemeAndReturnsFocus()
    {
        using var temp = new ScratchDirectory();
        const string content = "Привет!\nSecond line.";
        var path = temp.File("left/readme.txt", content);
        var (window, left, _, _) = await Open(temp);
        try
        {
            await left.NavigateAsync(left.CurrentPath, path); left.FocusList(); Dispatcher.UIThread.RunJobs();
            Press(window, Key.T, RawInputModifiers.Control | RawInputModifiers.Shift);
            Press(window, Key.F3);
            await WaitUntil(() => window.OwnedWindows.OfType<TextViewerWindow>().Any());
            var viewer = Assert.IsType<TextViewerWindow>(window.OwnedWindows.Single());
            var input = viewer.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.True(input.IsReadOnly);
            Assert.Equal(content, input.Text);
            Assert.Equal(window.ActualThemeVariant, viewer.ActualThemeVariant);
            viewer.KeyTextInput("must not modify");
            Assert.Equal(content, input.Text);
            var wrap = viewer.GetVisualDescendants().OfType<CheckBox>().Single();
            wrap.IsChecked = true;
            Assert.Equal(Avalonia.Media.TextWrapping.Wrap, input.TextWrapping);
            Press(viewer, Key.Escape);
            await WaitUntil(() => left.IsEnabled);
            Assert.IsType<ListBoxItem>(window.FocusManager!.GetFocusedElement());
            Assert.Equal(content, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task F3RejectsBinaryAndRestoresPaneWithoutOpeningViewer()
    {
        using var temp = new ScratchDirectory();
        var path = temp.File("left/data.bin", "\0\u0001binary");
        var (window, left, _, _) = await Open(temp);
        try
        {
            await left.NavigateAsync(left.CurrentPath, path); left.FocusList(); Dispatcher.UIThread.RunJobs();
            Press(window, Key.F3);
            await WaitUntil(() => left.IsEnabled);
            Assert.Empty(window.OwnedWindows);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("binary data") == true);
            Assert.IsType<ListBoxItem>(window.FocusManager!.GetFocusedElement());
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task ShiftF6RenamesCurrentEntryAndPreservesItsMark()
    {
        using var temp = new ScratchDirectory();
        var source = temp.File("left/a.txt"); temp.File("left/b.txt");
        var (window, left, right, _) = await Open(temp);
        try
        {
            await left.NavigateAsync(left.CurrentPath, source); left.FocusList(); Dispatcher.UIThread.RunJobs();
            Press(window, Key.Insert); Press(window, Key.Up);
            Press(window, Key.F6, RawInputModifiers.Shift);
            await WaitUntil(() => window.OwnedWindows.Any());
            var dialog = window.OwnedWindows.Single();
            Assert.Equal("Rename", dialog.Title);
            Assert.Equal("a.txt", dialog.GetVisualDescendants().OfType<TextBox>().Single().Text);
            dialog.KeyTextInput("renamed.txt"); Press(dialog, Key.Enter);
            await WaitUntil(() => left.IsEnabled);
            var renamed = Path.Combine(left.CurrentPath, "renamed.txt");
            Assert.False(File.Exists(source));
            Assert.True(File.Exists(renamed));
            Assert.Equal(renamed, left.CurrentEntry!.FullPath);
            Assert.Equal(renamed, Assert.Single(left.OperationEntries).FullPath);
            Assert.Empty(Directory.GetFileSystemEntries(right.CurrentPath));
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task ShiftF4ConfiguresAnEditorAndPersistsIt()
    {
        using var temp = new ScratchDirectory();
        var source = temp.File("left/a.txt");
        var launcher = new RecordingLauncher();
        var (window, left, _, settings) = await Open(temp, launcher: launcher);
        var editor = Path.Combine(temp.Root, "My Editor", "editor.exe");
        try
        {
            Press(window, Key.F4, RawInputModifiers.Shift);
            await WaitUntil(() => window.OwnedWindows.Any());
            var dialog = window.OwnedWindows.Single();
            Assert.Equal("External editor", dialog.Title);
            dialog.KeyTextInput(editor); Press(dialog, Key.Enter);
            await WaitUntil(() => left.IsEnabled);
            await left.NavigateAsync(left.CurrentPath, source); left.FocusList(); Dispatcher.UIThread.RunJobs();
            Press(window, Key.F4);
            await WaitUntil(() => left.IsEnabled);
            Assert.Equal((source, editor), Assert.Single(launcher.Edited));
        }
        finally { await Close(window); }
        Assert.Equal(editor, (await settings.LoadAsync()).EditorExecutable);
    }

    [AvaloniaFact]
    public async Task FailedExternalLaunchShowsErrorAndLeavesTheWindowUsable()
    {
        using var temp = new ScratchDirectory();
        var source = temp.File("left/a.txt");
        var launcher = new RecordingLauncher { Fail = true };
        var (window, left, _, _) = await Open(temp, launcher: launcher);
        try
        {
            await left.NavigateAsync(left.CurrentPath, source); left.FocusList(); Dispatcher.UIThread.RunJobs();
            Press(window, Key.Enter);
            await WaitUntil(() => left.IsEnabled);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Error: No file association.");
            launcher.Fail = false;
            Press(window, Key.F4);
            await WaitUntil(() => left.IsEnabled);
            Assert.Single(launcher.Edited);
        }
        finally { await Close(window); }
    }

    private sealed class RecordingLauncher : IExternalFileLauncher
    {
        public List<string> Opened { get; } = [];
        public List<(string Path, string? Editor)> Edited { get; } = [];
        public bool Fail { get; set; }
        public Task OpenAsync(string path, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("No file association.");
            Opened.Add(path); return Task.CompletedTask;
        }
        public Task EditAsync(string path, string? editorExecutable, CancellationToken cancellationToken = default)
        { Edited.Add((path, editorExecutable)); return Task.CompletedTask; }
    }

    private sealed class RecordingTrash : ITrashService
    {
        public bool IsSupported => true;
        public List<string> Paths { get; } = [];
        public Task RecycleAsync(string path, CancellationToken cancellationToken = default) { Paths.Add(path); return Task.CompletedTask; }
    }
}
