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
using DockCommander.Core.Connections;
using DockCommander.Core.Files;
using DockCommander.Core.Operations;
using DockCommander.App.Platform;
using Xunit;

namespace DockCommander.Tests;

public sealed class WindowTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelingConflictClosesItsDialogIncludingRename(bool rename)
    {
        using var temp = new ScratchDirectory();
        var sourcePath = temp.File("left/a.txt", "incoming");
        var destinationPath = temp.File("right/a.txt", "keep");
        var (window, _, _, _) = await Open(temp);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var provider = new LocalFileSystemProvider();
            var conflict = new FileConflict((await provider.GetEntryAsync(sourcePath))!,
                (await provider.GetEntryAsync(destinationPath))!);
            var pending = Dialogs.ConflictAsync(window, conflict, cancellation.Token);
            await WaitUntil(() => window.OwnedWindows.Any());
            if (rename)
            {
                window.OwnedWindows.Single().GetVisualDescendants().OfType<Button>()
                    .Single(button => Equals(button.Content, "Rename"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntil(() => window.OwnedWindows.Any(dialog => dialog.Title == "Rename incoming entry"));
            }
            await Task.Run(() => cancellation.Cancel());
            await WaitUntil(() => pending.IsCompleted && !window.OwnedWindows.Any());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal("keep", File.ReadAllText(destinationPath));
        }
        finally { cancellation.Cancel(); await Close(window); }
    }

    [AvaloniaFact]
    public async Task WindowRefusesToCloseDuringTransferAndClosesAfterEscapeCancelsIt()
    {
        using var temp = new ScratchDirectory();
        var source = temp.File("left/a.txt", "incoming");
        var provider = new GatedTransferProvider(new LocalFileSystemProvider());
        var (window, left, right, _) = await Open(temp, provider: provider);
        try
        {
            await left.NavigateAsync(left.CurrentPath, source); left.FocusList();
            Press(window, Key.F5);
            await WaitUntil(() => window.OwnedWindows.Any());
            Press(window.OwnedWindows.Single(), Key.Enter);
            await WaitUntil(() => provider.Transfers == 1 && left.IsEnabled);
            window.Close();
            Assert.True(window.IsVisible);
            Press(window, Key.Escape);
            await WaitUntilReady(window, left);
            Assert.True(File.Exists(source));
            Assert.False(File.Exists(Path.Combine(right.CurrentPath, "a.txt")));
            window.Close();
            await WaitUntil(() => !window.IsVisible);
        }
        finally { provider.Release(); if (window.IsVisible) await Close(window); }
    }

    [AvaloniaFact]
    public async Task SlowLocationDiscoveryDoesNotBlockFoldersOrKeyboardAndLoadsBothPanesOnce()
    {
        using var temp = new ScratchDirectory();
        var gate = new TaskCompletionSource<IReadOnlyList<LocationEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var (window, left, right, _) = await Open(temp, locations: token =>
        {
            calls++;
            return gate.Task.WaitAsync(token);
        });
        try
        {
            Assert.False(gate.Task.IsCompleted);
            Assert.Equal(1, calls);
            Assert.Equal(Path.Combine(temp.Root, "left"), left.CurrentPath);
            Assert.Equal(Path.Combine(temp.Root, "right"), right.CurrentPath);
            Press(window, Key.F7);
            await WaitUntil(() => window.OwnedWindows.Any());
            Press(window.OwnedWindows.Single(), Key.Escape);
            await WaitUntil(() => left.IsEnabled);
            gate.SetResult([new("test", "Test location", temp.Root)]);
            await WaitUntil(() => new[] { left, right }.All(pane => pane.GetVisualDescendants().OfType<Button>()
                .Any(button => Avalonia.Automation.AutomationProperties.GetName(button) == "Test location")));
            Assert.Equal(1, calls);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task ClosingWindowCancelsPendingLocationDiscovery()
    {
        using var temp = new ScratchDirectory();
        var gate = new TaskCompletionSource<IReadOnlyList<LocationEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        var (window, _, _, _) = await Open(temp, locations: token =>
        {
            observed = token;
            return gate.Task.WaitAsync(token);
        });
        await Close(window);
        Assert.True(observed.IsCancellationRequested);
    }

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
            await WaitUntilReady(window, right);
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
            await WaitUntilReady(window, left);
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
            await WaitUntilReady(window, left);
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
            Assert.True(Item("Context_newfile").IsEnabled);
            Assert.Equal("New file · Shift+F4", Item("Context_newfile").Header);
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
            await WaitUntil(() => window.OwnedWindows.Any(w => w.Title == "Destination exists" &&
                w.FocusManager!.GetFocusedElement() is Button { Content: "Overwrite" }));
            var conflict = window.OwnedWindows.Single();
            Assert.Equal(WindowDecorations.None, conflict.WindowDecorations);
            Assert.Equal("Overwrite", Assert.IsType<Button>(conflict.FocusManager!.GetFocusedElement()).Content);
            Press(conflict, Key.Enter);
            await WaitUntilReady(window, left);
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

    private static Task WaitUntilReady(MainWindow window, FilePane pane) =>
        WaitUntil(() => pane.IsEnabled && window.TransfersIdle);

    private static async Task WaitUntil(Func<bool> predicate)
    {
        for (var i = 0; i < 500; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (predicate()) return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(predicate(), "The UI did not reach the expected state.");
    }

    private static async Task<string> SavedText(string path)
    {
        await WaitUntil(() => File.Exists(path));
        return File.ReadAllText(path);
    }

    [AvaloniaFact]
    public async Task RemotePaneKeepsItsOwnModeAndDoesNotOpenAConnection()
    {
        using var temp = new ScratchDirectory();
        var settings = new SettingsStore(Path.Combine(temp.Root, "settings.json"));
        await settings.SaveAsync(new(temp.Folder("left"), temp.Folder("right")));
        await ConnectionStore.Beside(settings).SaveAsync(
        [
            new("prod", "Production", ConnectionProtocols.Sftp, "server.example.com", 22, "root", "/var/www"),
            new("nas", "Old NAS", ConnectionProtocols.WebDav, "192.168.1.20", 443, "reader")
        ]);
        var window = new MainWindow(settings, new FixedLocationsProvider(new LocalFileSystemProvider(),
            _ => Task.FromResult<IReadOnlyList<LocationEntry>>([new("t", "Test", temp.Root)])));
        window.Show();
        try
        {
            var panes = window.GetVisualDescendants().OfType<FilePane>().ToArray();
            await WaitUntil(() => panes.All(pane => pane.IsEnabled && pane.CurrentPath.StartsWith(temp.Root, StringComparison.Ordinal)));
            panes[0].GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(panes[0].IsRemote);
            Assert.False(panes[1].IsRemote);
            var files = panes[0].GetVisualDescendants().OfType<ListBox>().Single(list => list.Name != "ConnectionList");
            Assert.False(files.IsEffectivelyVisible);
            var search = panes[0].GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "SearchConnections");
            Assert.True(search.IsEffectivelyVisible);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Contains(panes[0].GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Production");
            Assert.Contains(panes[0].GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "SFTP · server.example.com:22");
            search.Text = "nas";
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Contains(panes[0].GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Old NAS");
            Assert.DoesNotContain(panes[0].GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Production");
            panes[0].GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Connect"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => window.OwnedWindows.Any());
            Click(window.OwnedWindows.Single(), "Cancel");
            await WaitUntil(() => !window.OwnedWindows.Any() && panes[0].IsRemote);
            Press(window, Key.F5);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                block => block.Text == "This pane is showing connections. Switch it to Local for files.");
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task DoubleClickingAConnectionRowStartsConnect()
    {
        using var temp = new ScratchDirectory();
        var settings = new SettingsStore(Path.Combine(temp.Root, "settings.json"));
        await settings.SaveAsync(new(temp.Folder("left"), temp.Folder("right")));
        await ConnectionStore.Beside(settings).SaveAsync(
            [new("prod", "Production", ConnectionProtocols.Sftp, "server.example.com", 22, "root", "/")]);
        var window = new MainWindow(settings, new FixedLocationsProvider(new LocalFileSystemProvider(),
            _ => Task.FromResult<IReadOnlyList<LocationEntry>>([new("t", "Test", temp.Root)])));
        window.Show();
        try
        {
            var pane = window.GetVisualDescendants().OfType<FilePane>().First();
            await WaitUntil(() => pane.IsEnabled);
            pane.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => pane.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "Production"));
            var name = pane.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == "Production");
            name.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));
            await WaitUntil(() => window.OwnedWindows.Any());
            Assert.NotNull(window.OwnedWindows.Single().GetVisualDescendants().OfType<TextBox>().SingleOrDefault(box => box.Name == "SftpPassword"));
            var edit = pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Edit"));
            edit.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Single(window.OwnedWindows);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task SftpConnectOpensThatPaneAndDoesNotStoreThePassword()
    {
        using var temp = new ScratchDirectory();
        var settings = new SettingsStore(Path.Combine(temp.Root, "settings.json"));
        await settings.SaveAsync(new(temp.Folder("left"), temp.Folder("right")));
        await ConnectionStore.Beside(settings).SaveAsync(
        [new("prod", "Production", ConnectionProtocols.Sftp, "server.example.com", 22, "root", "/")]);
        var remote = new MemorySftp();
        remote.AddDirectory("/");
        remote.AddFile("/hello.txt", "hi");
        var connector = new RecordingConnector(remote);
        var window = new MainWindow(settings, new FixedLocationsProvider(new LocalFileSystemProvider(),
            _ => Task.FromResult<IReadOnlyList<LocationEntry>>([new("t", "Test", temp.Root)])), sftp: connector);
        window.Show();
        try
        {
            var panes = window.GetVisualDescendants().OfType<FilePane>().ToArray();
            await WaitUntil(() => panes.All(pane => pane.IsEnabled && pane.CurrentPath.StartsWith(temp.Root, StringComparison.Ordinal)));
            panes[0].GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            panes[0].GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Connect"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => window.OwnedWindows.Any());
            var dialog = window.OwnedWindows.Single();
            dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "SftpPassword").Text = "session-secret";
            dialog.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "RememberSecret").IsChecked = false;
            dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Connect"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() =>
            {
                window.UpdateLayout();
                return panes[0].IsEnabled && !panes[0].IsRemote &&
                    panes[0].GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "hello.txt");
            });
            Assert.False(panes[1].IsRemote);
            Assert.Equal("/", panes[0].CurrentDisplayPath);
            Assert.StartsWith(temp.Root, panes[1].CurrentPath, StringComparison.Ordinal);
            Assert.Equal("session-secret", connector.Password);
            Assert.Contains("chosen", panes[0].GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode").Classes);
            Assert.DoesNotContain("session-secret", await SavedText(ConnectionStore.Beside(settings).Path));
            Assert.False(File.Exists(SecretStore.Beside(settings).Path));
            Assert.DoesNotContain(panes[1].GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Production" && block.IsEffectivelyVisible);
            panes[0].GetVisualDescendants().OfType<Button>().Single(button => button.Name == "LocalMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => panes[0].IsEnabled && panes[0].CurrentPath.StartsWith(temp.Root, StringComparison.Ordinal));
            panes[1].GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() =>
            {
                window.UpdateLayout();
                return panes[1].IsEnabled && !panes[1].IsRemote &&
                    panes[1].GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "hello.txt" && block.IsEffectivelyVisible);
            });
            Assert.Empty(window.OwnedWindows);
            Assert.Equal(1, connector.Calls);
            panes[0].GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => panes[0].IsEnabled && !panes[0].IsRemote &&
                panes[0].GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "hello.txt" && block.IsEffectivelyVisible));
            var plus = panes[0].GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SavedConnections");
            Assert.True(plus.IsVisible);
            plus.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => panes[0].IsRemote && panes[0].GetVisualDescendants().OfType<TextBox>().Any(box => box.Name == "SearchConnections" && box.IsEffectivelyVisible));
            panes[0].GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => panes[0].IsEnabled && !panes[0].IsRemote &&
                panes[0].GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "hello.txt" && block.IsEffectivelyVisible));
            panes[0].GetVisualDescendants().OfType<Button>().Single(button => button.Name == "LocalMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => panes[0].IsEnabled && panes[0].CurrentPath.StartsWith(temp.Root, StringComparison.Ordinal));
            Assert.Contains("chosen", panes[0].GetVisualDescendants().OfType<Button>().Single(button => button.Name == "LocalMode").Classes);
            Assert.Empty(window.OwnedWindows);
            panes[0].GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => panes[0].IsEnabled && !panes[0].IsRemote &&
                panes[0].GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "hello.txt" && block.IsEffectivelyVisible));
            Assert.Equal("session-secret", connector.Password);
            Assert.Equal(1, connector.Calls);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task SavingAConnectionShowsItImmediately()
    {
        using var temp = new ScratchDirectory();
        var settings = new SettingsStore(Path.Combine(temp.Root, "settings.json"));
        await settings.SaveAsync(new(temp.Folder("left"), temp.Folder("right")));
        var window = new MainWindow(settings, new FixedLocationsProvider(new LocalFileSystemProvider(),
            _ => Task.FromResult<IReadOnlyList<LocationEntry>>([new("t", "Test", temp.Root)])));
        window.Show();
        try
        {
            var pane = window.GetVisualDescendants().OfType<FilePane>().First();
            await WaitUntil(() => pane.IsEnabled && pane.CurrentPath.StartsWith(temp.Root, StringComparison.Ordinal));
            pane.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            pane.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "AddConnection")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => window.OwnedWindows.Any());
            var dialog = window.OwnedWindows.Single();
            dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "Name").Text = "DotHost";
            dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "Host").Text = "ftp.example.com";
            dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() =>
            {
                window.UpdateLayout();
                return pane.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "DotHost");
            });
            Assert.Contains("DotHost", await SavedText(ConnectionStore.Beside(settings).Path));
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task WebDavPresetSavesTheServerUrlAndNotTheInternalPath()
    {
        using var temp = new ScratchDirectory();
        var settings = new SettingsStore(Path.Combine(temp.Root, "settings.json"));
        await settings.SaveAsync(new(temp.Folder("left"), temp.Folder("right")));
        var window = new MainWindow(settings, new FixedLocationsProvider(new LocalFileSystemProvider(),
            _ => Task.FromResult<IReadOnlyList<LocationEntry>>([new("t", "Test", temp.Root)])));
        window.Show();
        try
        {
            var pane = window.GetVisualDescendants().OfType<FilePane>().First();
            await WaitUntil(() => pane.IsEnabled && pane.CurrentPath.StartsWith(temp.Root, StringComparison.Ordinal));
            pane.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            pane.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "AddConnection")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => window.OwnedWindows.Any());
            var dialog = window.OwnedWindows.Single();
            dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "Name").Text = "Home Cloud";
            var protocol = dialog.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Items.Cast<object?>().Contains(ConnectionProtocols.WebDav) && box.Name != "WebDavType");
            protocol.SelectedItem = ConnectionProtocols.WebDav;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.False(dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "Host").IsEffectivelyVisible);
            var type = dialog.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "WebDavType");
            Assert.True(type.IsEffectivelyVisible);
            type.SelectedItem = WebDavPresets.Nextcloud;
            var server = dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "ServerUrl");
            Assert.Equal("https://cloud.example.com/", server.PlaceholderText);
            server.Text = "https://cloud.example.com/";
            dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "Username").Text = "denis";
            dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() =>
            {
                window.UpdateLayout();
                return pane.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "Home Cloud");
            });
            var saved = await SavedText(ConnectionStore.Beside(settings).Path);
            Assert.Contains("\"WebDavPreset\": \"Nextcloud\"", saved);
            Assert.Contains("https://cloud.example.com/", saved);
            Assert.DoesNotContain("remote.php", saved);
            Assert.DoesNotContain("\"Password\":", saved);
            Assert.DoesNotContain("\"Passphrase\":", saved);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task FtpConnectOpensThatPaneAndDoesNotStoreThePassword()
    {
        using var temp = new ScratchDirectory();
        var settings = new SettingsStore(Path.Combine(temp.Root, "settings.json"));
        await settings.SaveAsync(new(temp.Folder("left"), temp.Folder("right")));
        await ConnectionStore.Beside(settings).SaveAsync(
        [new("nas", "Old NAS", ConnectionProtocols.Ftp, "192.168.1.20", 21, "reader", "/")]);
        var remote = new MemorySftp();
        remote.AddDirectory("/");
        remote.AddFile("/notes.txt", "ftp");
        var connector = new RecordingConnector(remote, "ftp");
        var window = new MainWindow(settings, new FixedLocationsProvider(new LocalFileSystemProvider(),
            _ => Task.FromResult<IReadOnlyList<LocationEntry>>([new("t", "Test", temp.Root)])), ftp: connector);
        window.Show();
        try
        {
            var panes = window.GetVisualDescendants().OfType<FilePane>().ToArray();
            await WaitUntil(() => panes.All(pane => pane.IsEnabled && pane.CurrentPath.StartsWith(temp.Root, StringComparison.Ordinal)));
            panes[0].GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            panes[0].GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Connect"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => window.OwnedWindows.Any());
            var dialog = window.OwnedWindows.Single();
            dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "SftpPassword").Text = "ftp-secret";
            dialog.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "RememberSecret").IsChecked = false;
            dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Connect"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() =>
            {
                window.UpdateLayout();
                return panes[0].IsEnabled && !panes[0].IsRemote &&
                    panes[0].GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "notes.txt");
            });
            Assert.False(panes[1].IsRemote);
            Assert.Equal("ftp-secret", connector.Password);
            Assert.DoesNotContain("ftp-secret", await SavedText(ConnectionStore.Beside(settings).Path));
            Assert.StartsWith(temp.Root, panes[1].CurrentPath, StringComparison.Ordinal);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task WebDavConnectOpensThatPaneAndDoesNotStoreThePassword()
    {
        using var temp = new ScratchDirectory();
        var settings = new SettingsStore(Path.Combine(temp.Root, "settings.json"));
        await settings.SaveAsync(new(temp.Folder("left"), temp.Folder("right")));
        await ConnectionStore.Beside(settings).SaveAsync(
        [new("cloud", "Cloud", ConnectionProtocols.WebDav, "cloud.example.com", 443, "denis", "/dav")]);
        var remote = new MemorySftp();
        remote.AddDirectory("/");
        remote.AddDirectory("/dav");
        remote.AddFile("/dav/notes.txt", "webdav");
        var connector = new RecordingConnector(remote, "webdav");
        var window = new MainWindow(settings, new FixedLocationsProvider(new LocalFileSystemProvider(),
            _ => Task.FromResult<IReadOnlyList<LocationEntry>>([new("t", "Test", temp.Root)])), webDav: connector);
        window.Show();
        try
        {
            var panes = window.GetVisualDescendants().OfType<FilePane>().ToArray();
            await WaitUntil(() => panes.All(pane => pane.IsEnabled && pane.CurrentPath.StartsWith(temp.Root, StringComparison.Ordinal)));
            panes[0].GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            panes[0].GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Connect"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => window.OwnedWindows.Any());
            var dialog = window.OwnedWindows.Single();
            dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "SftpPassword").Text = "dav-secret";
            dialog.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "RememberSecret").IsChecked = false;
            dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Connect"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() =>
            {
                window.UpdateLayout();
                return panes[0].IsEnabled && !panes[0].IsRemote &&
                    panes[0].GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "notes.txt");
            });
            Assert.Equal("/dav", panes[0].CurrentDisplayPath);
            Assert.Equal("dav-secret", connector.Password);
            Assert.DoesNotContain("dav-secret", await SavedText(ConnectionStore.Beside(settings).Path));
            Assert.False(panes[1].IsRemote);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task RememberedPasswordIsEncryptedAndReusedWithoutAnotherPrompt()
    {
        using var temp = new ScratchDirectory();
        var settings = new SettingsStore(Path.Combine(temp.Root, "settings.json"));
        await settings.SaveAsync(new(temp.Folder("left"), temp.Folder("right")));
        await ConnectionStore.Beside(settings).SaveAsync(
        [new("prod", "Production", ConnectionProtocols.Sftp, "server.example.com", 22, "root", "/")]);
        var remote = new MemorySftp();
        remote.AddDirectory("/");
        remote.AddFile("/hello.txt", "hi");
        var first = new RecordingConnector(remote);
        var window = new MainWindow(settings, new FixedLocationsProvider(new LocalFileSystemProvider(),
            _ => Task.FromResult<IReadOnlyList<LocationEntry>>([new("t", "Test", temp.Root)])), sftp: first);
        window.Show();
        try
        {
            await SubmitPassword(window, "session-secret", remember: true);
            await WaitUntil(() =>
            {
                window.UpdateLayout();
                return window.GetVisualDescendants().OfType<FilePane>().First().IsEnabled &&
                    window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "hello.txt");
            });
            var secrets = await File.ReadAllTextAsync(SecretStore.Beside(settings).Path, TestContext.Current.CancellationToken);
            var connections = await File.ReadAllTextAsync(ConnectionStore.Beside(settings).Path, TestContext.Current.CancellationToken);
            Assert.True(secrets.Contains("dpapi:", StringComparison.Ordinal) || secrets.Contains("aes1:", StringComparison.Ordinal));
            Assert.DoesNotContain("session-secret", secrets);
            Assert.DoesNotContain("session-secret", connections);
        }
        finally { await Close(window); }

        var second = new RecordingConnector(remote);
        var again = new MainWindow(settings, new FixedLocationsProvider(new LocalFileSystemProvider(),
            _ => Task.FromResult<IReadOnlyList<LocationEntry>>([new("t", "Test", temp.Root)])), sftp: second);
        again.Show();
        try
        {
            var pane = again.GetVisualDescendants().OfType<FilePane>().First();
            await WaitUntil(() => pane.IsEnabled);
            pane.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            again.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Connect"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() =>
            {
                again.UpdateLayout();
                return pane.IsEnabled && !pane.IsRemote && !again.OwnedWindows.Any() &&
                    pane.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "hello.txt");
            });
            Assert.Equal("session-secret", second.Password);
            Assert.Equal(1, second.Calls);
        }
        finally { await Close(again); }
    }

    [AvaloniaFact]
    public async Task ASecondSessionStaysBesideTheFirstAndCanBeClosed()
    {
        using var temp = new ScratchDirectory();
        var settings = new SettingsStore(Path.Combine(temp.Root, "settings.json"));
        await settings.SaveAsync(new(temp.Folder("left"), temp.Folder("right")));
        await ConnectionStore.Beside(settings).SaveAsync(
        [
            new("prod", "Production", ConnectionProtocols.Sftp, "server.example.com", 22, "root", "/"),
            new("backup", "Backup", ConnectionProtocols.Sftp, "backup.example.com", 22, "root", "/")
        ]);
        var production = new MemorySftp();
        production.AddDirectory("/");
        production.AddFile("/hello.txt", "hi");
        var backup = new MemorySftp();
        backup.AddDirectory("/");
        backup.AddFile("/notes.txt", "notes");
        var connector = new MappedConnector(new Dictionary<string, MemorySftp> { ["prod"] = production, ["backup"] = backup });
        var window = new MainWindow(settings, new FixedLocationsProvider(new LocalFileSystemProvider(),
            _ => Task.FromResult<IReadOnlyList<LocationEntry>>([new("t", "Test", temp.Root)])), sftp: connector);
        window.Show();
        try
        {
            var panes = window.GetVisualDescendants().OfType<FilePane>().ToArray();
            await OpenCatalog(panes[0]);
            await SignIn(window, panes[0], "Production", "one");
            await WaitUntil(() => SeesFile(window, "hello.txt"));
            var plus = panes[0].GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SavedConnections");
            plus.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => panes[0].IsRemote);
            await SignIn(window, panes[0], "Backup", "two");
            await WaitUntil(() => SeesFile(window, "notes.txt") && SessionNames(panes[0]).SequenceEqual(["Backup", "Production"]));
            Assert.Contains("chosen", panes[0].GetVisualDescendants().OfType<Button>().Single(button => button.Name == "OpenSession-backup").Classes);
            panes[1].GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => panes[1].IsEnabled && panes[1].IsRemote && SessionNames(panes[1]).SequenceEqual(["Backup", "Production"]));
            panes[1].GetVisualDescendants().OfType<Button>().Single(button => button.Name == "OpenSession-prod")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() =>
                panes[1].IsEnabled && !panes[1].IsRemote &&
                panes[1].GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "hello.txt") &&
                panes[0].GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "notes.txt"));
            var close = panes[0].GetVisualDescendants().OfType<Button>().Single(button => button.Name == "OpenSession-backup")
                .ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Close"));
            close.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntil(() =>
                panes[0].IsRemote &&
                !panes[0].GetVisualDescendants().OfType<Button>().Any(button => button.Name == "OpenSession-backup") &&
                panes[1].GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "hello.txt") &&
                panes[1].GetVisualDescendants().OfType<Button>().Any(button => button.Name == "OpenSession-prod"));
            Assert.Equal(2, connector.Calls);
        }
        finally { await Close(window); }

        static async Task OpenCatalog(FilePane pane)
        {
            await WaitUntil(() => pane.IsEnabled);
            pane.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            pane.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        static async Task SignIn(MainWindow owner, FilePane pane, string name, string password)
        {
            var label = pane.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == name);
            var row = label.GetVisualAncestors().OfType<Grid>().First(grid =>
                grid.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, "Connect")));
            row.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, "Connect"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => owner.OwnedWindows.Any());
            var dialog = owner.OwnedWindows.Single();
            dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "SftpPassword").Text = password;
            dialog.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "RememberSecret").IsChecked = false;
            Click(dialog, "Connect");
        }

        static string[] SessionNames(FilePane pane) => pane.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Name?.StartsWith("OpenSession-", StringComparison.Ordinal) == true)
            .Select(button => button.Name!["OpenSession-".Length..])
            .Select(id => id == "backup" ? "Backup" : "Production")
            .ToArray();
    }

    [AvaloniaFact]
    public async Task DisconnectKeepsTheProfileAndADroppedServerEndsTheLiveSession()
    {
        using var temp = new ScratchDirectory();
        var settings = new SettingsStore(Path.Combine(temp.Root, "settings.json"));
        await settings.SaveAsync(new(temp.Folder("left"), temp.Folder("right")));
        await ConnectionStore.Beside(settings).SaveAsync(
            [new("nas", "Old NAS", ConnectionProtocols.Sftp, "nas.example.com", 22, "reader", "/")]);
        var remote = new MemorySftp();
        remote.AddDirectory("/");
        remote.AddFile("/notes.txt", "keep");
        var connector = new RecordingConnector(remote);
        var window = new MainWindow(settings, new FixedLocationsProvider(new LocalFileSystemProvider(),
            _ => Task.FromResult<IReadOnlyList<LocationEntry>>([new("t", "Test", temp.Root)])), sftp: connector);
        window.Show();
        try
        {
            await SubmitPassword(window, "saved-secret", remember: true);
            await WaitUntil(() => window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "notes.txt"));
            var secrets = SecretStore.Beside(settings).Path;
            Assert.True(File.Exists(secrets));
            Menu("Disconnect").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntil(() => window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "Old NAS · Disconnected")
                && window.GetVisualDescendants().OfType<FilePane>().First().IsRemote);
            Assert.Contains("Old NAS", await SavedText(ConnectionStore.Beside(settings).Path));
            Assert.True(File.Exists(secrets));
            Menu("Reconnect").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntil(() => connector.Calls == 2 && FilesAreVisible());
            Assert.Empty(window.OwnedWindows);
            remote.FailClosed = true;
            Press(window, Key.R, RawInputModifiers.Control);
            await WaitUntil(() => window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "Old NAS · Disconnected")
                && window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "Disconnected: The SFTP connection closed."));
            remote.FailClosed = false;
            window.GetVisualDescendants().OfType<FilePane>().First().GetVisualDescendants().OfType<Button>()
                .Single(button => button.Name == "OpenSession-nas")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => connector.Calls == 3 && FilesAreVisible());
            Menu("Forget sign-in").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntil(() => window.OwnedWindows.Any());
            Click(window.OwnedWindows.Single(), "Forget");
            await WaitUntil(() => !File.Exists(secrets));
            Menu("Close").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntil(() => !window.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "OpenSession-nas"));
            Assert.Contains("Old NAS", await SavedText(ConnectionStore.Beside(settings).Path));
        }
        finally { await Close(window); }

        MenuItem Menu(string caption) => window.GetVisualDescendants().OfType<FilePane>().First()
            .GetVisualDescendants().OfType<Button>().Single(button => button.Name == "OpenSession-nas")
            .ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, caption));

        bool FilesAreVisible()
        {
            var pane = window.GetVisualDescendants().OfType<FilePane>().First();
            var files = pane.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name != "ConnectionList");
            return files.IsEffectivelyVisible && pane.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "notes.txt");
        }
    }

    private sealed class MappedConnector(IReadOnlyDictionary<string, MemorySftp> remotes) : ISftpConnector
    {
        public int Calls { get; private set; }

        public Task<IFileSystemProvider> ConnectAsync(ConnectionProfile profile, SftpCredentials credentials,
            Func<string, bool> trustHost, CancellationToken cancellationToken) => Task.Run<IFileSystemProvider>(() =>
        {
            Calls++;
            return new SftpFileSystemProvider(remotes[profile.Id], profile.Id, profile.Name, profile.InitialPath);
        });
    }

    [AvaloniaFact]
    public async Task FailedSignInOpensThePasswordWindowAgain()
    {
        using var temp = new ScratchDirectory();
        var settings = new SettingsStore(Path.Combine(temp.Root, "settings.json"));
        await settings.SaveAsync(new(temp.Folder("left"), temp.Folder("right")));
        await ConnectionStore.Beside(settings).SaveAsync(
        [new("prod", "Production", ConnectionProtocols.Sftp, "server.example.com", 22, "root", "/")]);
        var remote = new MemorySftp();
        remote.AddDirectory("/");
        remote.AddFile("/hello.txt", "hi");
        var locations = new FixedLocationsProvider(new LocalFileSystemProvider(),
            _ => Task.FromResult<IReadOnlyList<LocationEntry>>([new("t", "Test", temp.Root)]));
        var first = new RecordingConnector(remote);
        var window = new MainWindow(settings, locations, sftp: first);
        window.Show();
        try { await SubmitPassword(window, "session-secret", remember: true); await WaitUntil(() => SeesFile(window, "hello.txt")); }
        finally { await Close(window); }

        var second = new RecordingConnector(remote) { FailTimes = 1 };
        var again = new MainWindow(settings, locations, sftp: second);
        again.Show();
        try
        {
            var pane = again.GetVisualDescendants().OfType<FilePane>().First();
            await WaitUntil(() => pane.IsEnabled);
            pane.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            again.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Connect"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => again.OwnedWindows.Any(owned =>
                owned.GetVisualDescendants().OfType<TextBox>().Any(box => box.Name == "SftpPassword" && box.Text == "session-secret")));
            Click(again.OwnedWindows.Single(), "Connect");
            await WaitUntil(() => SeesFile(again, "hello.txt") && !again.OwnedWindows.Any());
            Assert.Equal(2, second.Calls);
            Assert.Equal("session-secret", second.Password);
        }
        finally { await Close(again); }
    }

    private static bool SeesFile(MainWindow window, string name)
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<FilePane>().First().IsEnabled &&
            window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == name);
    }

    [AvaloniaFact]
    public async Task ServerTrustIsRememberedUntilTheIdentityChanges()
    {
        using var temp = new ScratchDirectory();
        var settings = new SettingsStore(Path.Combine(temp.Root, "settings.json"));
        await settings.SaveAsync(new(temp.Folder("left"), temp.Folder("right")));
        await ConnectionStore.Beside(settings).SaveAsync(
        [new("prod", "Production", ConnectionProtocols.Sftp, "server.example.com", 22, "root", "/")]);
        var remote = new MemorySftp();
        remote.AddDirectory("/");
        remote.AddFile("/hello.txt", "hi");
        var first = new RecordingConnector(remote) { ChallengeTrust = true, Fingerprint = "ssh-ed25519 SHA256:abc" };
        var window = new MainWindow(settings, new FixedLocationsProvider(new LocalFileSystemProvider(),
            _ => Task.FromResult<IReadOnlyList<LocationEntry>>([new("t", "Test", temp.Root)])), sftp: first);
        window.Show();
        try
        {
            await SubmitPassword(window, "session-secret", remember: true);
            await WaitUntil(() => window.OwnedWindows.Any(owned => owned.Title == "Trust server"));
            Click(window.OwnedWindows.Single(), "Trust");
            await WaitUntil(() =>
            {
                window.UpdateLayout();
                return window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "hello.txt") &&
                    File.Exists(ConnectionStore.Beside(settings).Path) && File.ReadAllText(ConnectionStore.Beside(settings).Path).Contains("SHA256:abc", StringComparison.Ordinal);
            });
        }
        finally { await Close(window); }

        var second = new RecordingConnector(remote) { ChallengeTrust = true, Fingerprint = "ssh-ed25519 SHA256:abc" };
        var again = new MainWindow(settings, new FixedLocationsProvider(new LocalFileSystemProvider(),
            _ => Task.FromResult<IReadOnlyList<LocationEntry>>([new("t", "Test", temp.Root)])), sftp: second);
        again.Show();
        try
        {
            var pane = again.GetVisualDescendants().OfType<FilePane>().First();
            await WaitUntil(() => pane.IsEnabled);
            pane.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            again.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Connect"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() =>
            {
                again.UpdateLayout();
                return pane.IsEnabled && !pane.IsRemote && !again.OwnedWindows.Any() &&
                    pane.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "hello.txt");
            });
            Assert.Equal(1, second.Calls);
        }
        finally { await Close(again); }

        var third = new RecordingConnector(remote) { ChallengeTrust = true, Fingerprint = "ssh-ed25519 SHA256:changed" };
        var changed = new MainWindow(settings, new FixedLocationsProvider(new LocalFileSystemProvider(),
            _ => Task.FromResult<IReadOnlyList<LocationEntry>>([new("t", "Test", temp.Root)])), sftp: third);
        changed.Show();
        try
        {
            var pane = changed.GetVisualDescendants().OfType<FilePane>().First();
            await WaitUntil(() => pane.IsEnabled);
            pane.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            changed.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Connect"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => changed.OwnedWindows.Any(owned => owned.Title == "Trust server"));
            Click(changed.OwnedWindows.Single(), "Trust");
            await WaitUntil(() =>
                File.Exists(ConnectionStore.Beside(settings).Path) && File.ReadAllText(ConnectionStore.Beside(settings).Path).Contains("SHA256:changed", StringComparison.Ordinal));
        }
        finally { await Close(changed); }
    }

    [AvaloniaFact]
    public async Task AChangedCertificateIsAskedAgainEvenWhenTheNewOneIsValid()
    {
        using var temp = new ScratchDirectory();
        var settings = new SettingsStore(Path.Combine(temp.Root, "settings.json"));
        await settings.SaveAsync(new(temp.Folder("left"), temp.Folder("right")));
        await ConnectionStore.Beside(settings).SaveAsync(
        [new("nas", "Old NAS", ConnectionProtocols.Ftp, "192.168.1.20", 21, "reader", "/")]);
        var remote = new MemorySftp();
        remote.AddDirectory("/");
        remote.AddFile("/notes.txt", "ftp");
        var locations = new FixedLocationsProvider(new LocalFileSystemProvider(),
            _ => Task.FromResult<IReadOnlyList<LocationEntry>>([new("t", "Test", temp.Root)]));
        var first = new RecordingConnector(remote, "ftp") { ChallengeTrust = true, SystemTrusted = true, Fingerprint = "AAAA1111" };
        var window = new MainWindow(settings, locations, ftp: first);
        window.Show();
        try
        {
            await SubmitPassword(window, "ftp-secret", remember: false);
            await WaitUntil(() =>
            {
                window.UpdateLayout();
                return !window.OwnedWindows.Any() &&
                    window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "notes.txt") &&
                    File.Exists(ConnectionStore.Beside(settings).Path) && File.ReadAllText(ConnectionStore.Beside(settings).Path).Contains("AAAA1111", StringComparison.Ordinal);
            });
        }
        finally { await Close(window); }

        var second = new RecordingConnector(remote, "ftp") { ChallengeTrust = true, SystemTrusted = true, Fingerprint = "BBBB2222" };
        var again = new MainWindow(settings, locations, ftp: second);
        again.Show();
        try
        {
            await SubmitPassword(again, "ftp-secret", remember: false);
            await WaitUntil(() => again.OwnedWindows.Any(owned => owned.Title == "Trust certificate"));
            Click(again.OwnedWindows.Single(), "Trust");
            await WaitUntil(() =>
                File.Exists(ConnectionStore.Beside(settings).Path) && File.ReadAllText(ConnectionStore.Beside(settings).Path).Contains("BBBB2222", StringComparison.Ordinal));
        }
        finally { await Close(again); }
    }

    private static async Task SubmitPassword(MainWindow window, string password, bool remember)
    {
        var pane = window.GetVisualDescendants().OfType<FilePane>().First();
        await WaitUntil(() => pane.IsEnabled);
        pane.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Connect"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => window.OwnedWindows.Any());
        var dialog = window.OwnedWindows.Single();
        dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "SftpPassword").Text = password;
        dialog.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "RememberSecret").IsChecked = remember;
        Click(dialog, "Connect");
    }

    private sealed class RecordingConnector(MemorySftp remote, string scheme = "sftp") : ISftpConnector, IFtpConnector, IWebDavConnector
    {
        public string? Password { get; private set; }
        public int Calls { get; private set; }
        public int FailTimes { get; set; }
        public bool ChallengeTrust { get; set; }
        public bool SystemTrusted { get; set; }
        public string Fingerprint { get; set; } = "ssh-ed25519 SHA256:abc";

        public Task<IFileSystemProvider> ConnectAsync(ConnectionProfile profile, SftpCredentials credentials,
            Func<string, bool> trustHost, CancellationToken cancellationToken) =>
            Finish(profile, credentials, () => !ChallengeTrust || trustHost(Fingerprint));

        public Task<IFileSystemProvider> ConnectAsync(ConnectionProfile profile, SftpCredentials credentials,
            Func<string, bool, bool> trustCertificate, CancellationToken cancellationToken) =>
            Finish(profile, credentials, () => !ChallengeTrust || trustCertificate(Fingerprint, SystemTrusted));

        private Task<IFileSystemProvider> Finish(ConnectionProfile profile, SftpCredentials credentials, Func<bool> accept) =>
            Task.Run<IFileSystemProvider>(() =>
            {
                Calls++;
                Password = credentials.Password;
                if (FailTimes > 0)
                {
                    FailTimes--;
                    throw new IOException("Authentication failed.");
                }
                if (!accept()) throw new IOException("The server was not trusted.");
                return new SftpFileSystemProvider(remote, profile.Id, profile.Name, profile.InitialPath, scheme);
            });
    }

    private static async Task<(MainWindow Window, FilePane Left, FilePane Right, SettingsStore Settings)> Open(ScratchDirectory temp, ITrashService? trash = null, IExternalFileLauncher? launcher = null, IFileSystemProvider? provider = null,
        Func<CancellationToken, Task<IReadOnlyList<LocationEntry>>>? locations = null)
    {
        var left = temp.Folder("left"); var right = temp.Folder("right");
        var store = new SettingsStore(Path.Combine(temp.Root, "settings.json"));
        await store.SaveAsync(new(left, right));
        var root = Path.GetPathRoot(temp.Root)!;
        var isolated = new FixedLocationsProvider(provider ?? new LocalFileSystemProvider(trash),
            locations ?? (_ => Task.FromResult<IReadOnlyList<LocationEntry>>([new(root, root, root)])));
        var window = new MainWindow(store, isolated, launcher);
        window.Show();
        var panes = window.GetVisualDescendants().OfType<FilePane>().ToArray();
        await WaitUntil(() => panes.All(p => p.IsEnabled && p.CurrentPath.StartsWith(temp.Root, StringComparison.Ordinal)));
        return (window, panes[0], panes[1], store);
    }

    private static async Task Close(MainWindow window)
    {
        foreach (var child in window.OwnedWindows.ToArray()) child.Close();
        await WaitUntil(() => window.TransfersIdle && window.GetVisualDescendants().OfType<FilePane>().All(p => p.IsEnabled));
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
            await WaitUntilReady(window, left);
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
    public async Task RemoteF8AsksForPermanentDeletion()
    {
        using var temp = new ScratchDirectory();
        var settings = new SettingsStore(Path.Combine(temp.Root, "settings.json"));
        await settings.SaveAsync(new(temp.Folder("left"), temp.Folder("right")));
        await ConnectionStore.Beside(settings).SaveAsync(
            [new("prod", "Production", ConnectionProtocols.Sftp, "server.example.com", 22, "root", "/")]);
        var remote = new MemorySftp();
        remote.AddDirectory("/");
        remote.AddFile("/hello.txt", "hi");
        var window = new MainWindow(settings, new FixedLocationsProvider(new LocalFileSystemProvider(),
            _ => Task.FromResult<IReadOnlyList<LocationEntry>>([new("t", "Test", temp.Root)])), sftp: new RecordingConnector(remote));
        window.Show();
        try
        {
            var pane = window.GetVisualDescendants().OfType<FilePane>().First();
            await WaitUntil(() => pane.IsEnabled && pane.CurrentPath.StartsWith(temp.Root, StringComparison.Ordinal));
            pane.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Connect"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => window.OwnedWindows.Any());
            var dialog = window.OwnedWindows.Single();
            dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "SftpPassword").Text = "secret";
            dialog.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "RememberSecret").IsChecked = false;
            dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Connect"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => pane.IsEnabled && !pane.IsRemote &&
                pane.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "hello.txt"));
            pane.FocusList(); Dispatcher.UIThread.RunJobs();
            Press(window, Key.F8);
            await WaitUntil(() => window.OwnedWindows.Any());
            Assert.Equal("Delete permanently", window.OwnedWindows.Single().Title);
            Press(window.OwnedWindows.Single(), Key.Enter);
            await WaitUntil(() => pane.IsEnabled && !pane.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "hello.txt"));
            Assert.Null(remote.TryStat("/hello.txt", CancellationToken.None));
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task DragAndExternalDropReachARemotePane()
    {
        using var temp = new ScratchDirectory();
        var settings = new SettingsStore(Path.Combine(temp.Root, "settings.json"));
        var right = temp.Folder("right");
        await settings.SaveAsync(new(temp.Folder("left"), right));
        await ConnectionStore.Beside(settings).SaveAsync(
            [new("prod", "Production", ConnectionProtocols.Sftp, "server.example.com", 22, "root", "/")]);
        var remote = new MemorySftp();
        remote.AddDirectory("/");
        remote.AddFile("/hello.txt", "hi");
        var localFile = temp.File("right/from-local.txt", "local-body");
        var outside = temp.File("outside.txt", "dropped");
        var window = new MainWindow(settings, new FixedLocationsProvider(new LocalFileSystemProvider(),
            _ => Task.FromResult<IReadOnlyList<LocationEntry>>([new("t", "Test", temp.Root)])), sftp: new RecordingConnector(remote));
        window.Show();
        try
        {
            var panes = window.GetVisualDescendants().OfType<FilePane>().ToArray();
            await WaitUntil(() => panes.All(pane => pane.IsEnabled && pane.CurrentPath.StartsWith(temp.Root, StringComparison.Ordinal)));
            panes[0].GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoteMode")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            panes[0].GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Connect"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => window.OwnedWindows.Any());
            var dialog = window.OwnedWindows.Single();
            dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "SftpPassword").Text = "secret";
            dialog.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "RememberSecret").IsChecked = false;
            dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Connect"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => panes[0].IsEnabled && !panes[0].IsRemote &&
                panes[0].GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "hello.txt"));
            await panes[1].NavigateAsync(panes[1].CurrentPath, localFile);
            var outgoing = new FilePane.DraggedEntries(panes[1], [(await panes[1].Provider.GetEntryAsync(localFile))!]);
            Assert.True(panes[0].CanAcceptDrop(outgoing, panes[0].CurrentPath));
            panes[0].RequestDrop(outgoing, panes[0].CurrentPath, false);
            await WaitUntil(() => window.OwnedWindows.Any());
            Assert.Equal("Copy", window.OwnedWindows.Single().Title);
            Press(window.OwnedWindows.Single(), Key.Enter);
            await WaitUntilReady(window, panes[0]);
            Assert.Equal("local-body", remote.ReadFile("/from-local.txt"));
            var incoming = new FilePane.DraggedEntries(panes[0], [(await panes[0].Provider.GetEntryAsync(panes[0].Provider.NormalizePath("/hello.txt")))!]);
            Assert.True(panes[1].CanAcceptDrop(incoming, panes[1].CurrentPath));
            panes[1].RequestDrop(incoming, panes[1].CurrentPath, false);
            await WaitUntil(() => window.OwnedWindows.Any());
            Press(window.OwnedWindows.Single(), Key.Enter);
            await WaitUntilReady(window, panes[1]);
            Assert.Equal("hi", File.ReadAllText(Path.Combine(right, "hello.txt")));
            panes[0].RequestExternalDrop([outside], panes[0].CurrentPath);
            await WaitUntil(() => window.OwnedWindows.Any());
            Press(window.OwnedWindows.Single(), Key.Enter);
            await WaitUntilReady(window, panes[0]);
            Assert.Equal("dropped", remote.ReadFile("/outside.txt"));
            Assert.True(File.Exists(outside));
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
            await WaitUntil(() => window.OwnedWindows.OfType<TextEditorWindow>().Any());
            var editor = Assert.IsType<TextEditorWindow>(window.OwnedWindows.Single());
            Assert.Contains("b.txt", editor.Title);
            Assert.Empty(launcher.Edited);
            Press(editor, Key.Escape);
            await WaitUntil(() => left.IsEnabled);
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
            Press(window, Key.F4, RawInputModifiers.Control | RawInputModifiers.Shift);
            await WaitUntil(() => window.OwnedWindows.Any());
            var dialog = window.OwnedWindows.Single();
            Assert.Equal("External editor", dialog.Title);
            dialog.KeyTextInput(editor); Press(dialog, Key.Enter);
            await WaitUntil(() => left.IsEnabled);
            await left.NavigateAsync(left.CurrentPath, source); left.FocusList(); Dispatcher.UIThread.RunJobs();
            left.GetVisualDescendants().OfType<ListBox>().Single().ContextMenu!.Items.OfType<MenuItem>()
                .Single(item => item.Name == "Context_external")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntil(() => left.IsEnabled);
            Assert.Equal((source, editor), Assert.Single(launcher.Edited));
            Press(window, Key.F4);
            await WaitUntil(() => window.OwnedWindows.OfType<TextEditorWindow>().Any());
            Assert.Single(launcher.Edited);
            Press(window.OwnedWindows.Single(), Key.Escape);
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
            left.GetVisualDescendants().OfType<ListBox>().Single().ContextMenu!.Items.OfType<MenuItem>()
                .Single(item => item.Name == "Context_external")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntil(() => left.IsEnabled);
            Assert.Single(launcher.Edited);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task F4EditsALocalFileAndCtrlSClearsTheDirtyState()
    {
        using var temp = new ScratchDirectory();
        var path = temp.File("left/notes.txt", "alpha beta alpha\nsecond");
        var (window, left, _, _) = await Open(temp);
        try
        {
            await left.NavigateAsync(left.CurrentPath, path); left.FocusList(); Dispatcher.UIThread.RunJobs();
            Press(window, Key.F4);
            await WaitUntil(() => window.OwnedWindows.OfType<TextEditorWindow>().Any());
            var editor = Assert.IsType<TextEditorWindow>(window.OwnedWindows.Single());
            var text = editor.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "EditorText");
            Assert.False(text.IsReadOnly);
            Assert.Equal("alpha beta alpha\nsecond", text.Text);
            Assert.False(editor.IsDirty);
            text.CaretIndex = text.Text!.IndexOf('\n') + 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Ln 2, Col 1", editor.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "CaretPosition").Text);
            var wrap = editor.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "WrapLines");
            wrap.IsChecked = true;
            Assert.Equal(Avalonia.Media.TextWrapping.Wrap, text.TextWrapping);
            wrap.IsChecked = false;
            Assert.Equal(Avalonia.Media.TextWrapping.NoWrap, text.TextWrapping);
            Press(editor, Key.F, RawInputModifiers.Control);
            var searchBar = editor.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "SearchBar");
            Assert.True(searchBar.IsVisible);
            editor.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "SearchText").Text = "alpha";
            Click(editor, "Next");
            Assert.Equal(0, Math.Min(text.SelectionStart, text.SelectionEnd));
            Click(editor, "Next");
            Assert.Equal(text.Text.LastIndexOf("alpha", StringComparison.Ordinal), Math.Min(text.SelectionStart, text.SelectionEnd));
            Click(editor, "Previous");
            Assert.Equal(0, Math.Min(text.SelectionStart, text.SelectionEnd));
            Press(editor, Key.Escape);
            Assert.False(searchBar.IsVisible);
            Assert.True(editor.IsVisible);
            text.Focus();
            text.CaretIndex = text.Text.Length;
            editor.KeyTextInput("!");
            Assert.EndsWith("!", text.Text);
            Press(editor, Key.Z, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.False(text.Text!.EndsWith('!'));
            Press(editor, Key.Y, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.EndsWith("!", text.Text);
            Press(editor, Key.S, RawInputModifiers.Control);
            await WaitUntil(() => File.ReadAllText(path).EndsWith('!'));
            Assert.False(editor.IsDirty);
            Assert.Contains(editor.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Saved.");
            Assert.Equal("alpha beta alpha\nsecond!", File.ReadAllText(path).Replace("\r\n", "\n"));
            Press(editor, Key.Escape);
            await WaitUntil(() => left.IsEnabled && !window.OwnedWindows.Any());
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Saved: notes.txt");
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task ClosingADirtyEditorAsksToSaveDiscardOrCancel()
    {
        using var temp = new ScratchDirectory();
        var path = temp.File("left/notes.txt", "keep");
        var (window, left, _, _) = await Open(temp);
        try
        {
            await left.NavigateAsync(left.CurrentPath, path); left.FocusList(); Dispatcher.UIThread.RunJobs();
            Press(window, Key.F4);
            await WaitUntil(() => window.OwnedWindows.OfType<TextEditorWindow>().Any());
            var editor = (TextEditorWindow)window.OwnedWindows.Single();
            var text = editor.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "EditorText");
            text.Text = "keep!";
            Press(editor, Key.Escape);
            await WaitUntil(() => editor.OwnedWindows.Any());
            var dialog = editor.OwnedWindows.Single();
            Assert.Contains("Save", dialog.Title);
            Assert.Equal("Save, Don't Save, Cancel", string.Join(", ", dialog.GetVisualDescendants().OfType<Button>().Select(button => button.Content as string).Where(caption => caption is "Save" or "Don't Save" or "Cancel")));
            Click(dialog, "Cancel");
            await WaitUntil(() => !editor.OwnedWindows.Any());
            Assert.True(editor.IsVisible);
            Assert.Equal("keep!", text.Text);
            Assert.Equal("keep", File.ReadAllText(path));
            Press(editor, Key.Escape);
            await WaitUntil(() => editor.OwnedWindows.Any());
            Click(editor.OwnedWindows.Single(), "Don't Save");
            await WaitUntil(() => left.IsEnabled && !window.OwnedWindows.Any());
            Assert.Equal("keep", File.ReadAllText(path));
            Press(window, Key.F4);
            await WaitUntil(() => window.OwnedWindows.OfType<TextEditorWindow>().Any());
            editor = (TextEditorWindow)window.OwnedWindows.Single();
            editor.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "EditorText").Text = "saved";
            Press(editor, Key.Escape);
            await WaitUntil(() => editor.OwnedWindows.Any());
            Click(editor.OwnedWindows.Single(), "Save");
            await WaitUntil(() => left.IsEnabled && File.ReadAllText(path) == "saved");
            Assert.Empty(window.OwnedWindows);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task FailedSaveKeepsTheTextAndTheDirtyState()
    {
        using var temp = new ScratchDirectory();
        var path = temp.File("left/notes.txt", "keep");
        var (window, left, _, _) = await Open(temp, provider: new RejectingWriter(new LocalFileSystemProvider()));
        try
        {
            await left.NavigateAsync(left.CurrentPath, path); left.FocusList(); Dispatcher.UIThread.RunJobs();
            Press(window, Key.F4);
            await WaitUntil(() => window.OwnedWindows.OfType<TextEditorWindow>().Any());
            var editor = (TextEditorWindow)window.OwnedWindows.Single();
            var text = editor.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "EditorText");
            text.Text = "keep more";
            Press(editor, Key.S, RawInputModifiers.Control);
            await WaitUntil(() => editor.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "The disk is full."));
            Assert.True(editor.IsDirty);
            Assert.Equal("keep more", text.Text);
            Assert.Equal("keep", File.ReadAllText(path));
            Press(editor, Key.Escape);
            await WaitUntil(() => editor.OwnedWindows.Any());
            Click(editor.OwnedWindows.Single(), "Don't Save");
            await WaitUntil(() => left.IsEnabled);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task ShiftF4CreatesAFileAndOpensItInTheEditor()
    {
        using var temp = new ScratchDirectory();
        var (window, left, _, _) = await Open(temp);
        try
        {
            left.FocusList(); Dispatcher.UIThread.RunJobs();
            Press(window, Key.F4, RawInputModifiers.Shift);
            await WaitUntil(() => window.OwnedWindows.Any());
            var dialog = window.OwnedWindows.Single();
            Assert.Equal("New file", dialog.Title);
            Press(dialog, Key.Escape);
            await WaitUntil(() => left.IsEnabled);
            Assert.Empty(Directory.GetFiles(left.CurrentPath));
            Press(window, Key.F4, RawInputModifiers.Shift);
            await WaitUntil(() => window.OwnedWindows.Any());
            window.OwnedWindows.Single().KeyTextInput("notes.txt");
            Press(window.OwnedWindows.Single(), Key.Enter);
            await WaitUntil(() => window.OwnedWindows.OfType<TextEditorWindow>().Any());
            var created = Path.Combine(left.CurrentPath, "notes.txt");
            Assert.True(File.Exists(created));
            Assert.Empty(await File.ReadAllTextAsync(created));
            var editor = Assert.IsType<TextEditorWindow>(window.OwnedWindows.Single());
            Assert.Equal("", editor.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "EditorText").Text);
            Press(editor, Key.Escape);
            await WaitUntil(() => left.IsEnabled && !window.OwnedWindows.Any());
            Assert.Equal(created, left.CurrentEntry!.FullPath);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task F4OpensUnsuitableFilesWithTheSystemAndKeepsReadOnlyTextClosed()
    {
        using var temp = new ScratchDirectory();
        var binary = temp.File("left/data.bin", "\0\u0001binary");
        var launcher = new RecordingLauncher();
        var (window, left, _, _) = await Open(temp, launcher: launcher);
        try
        {
            await left.NavigateAsync(left.CurrentPath, binary); left.FocusList(); Dispatcher.UIThread.RunJobs();
            Press(window, Key.F4);
            await WaitUntil(() => left.IsEnabled && launcher.Opened.Count == 1);
            Assert.Equal(binary, launcher.Opened[0]);
            Assert.Empty(window.OwnedWindows);
            Assert.Empty(launcher.Edited);
            var huge = temp.File("left/huge.txt", new string('a', TextDocuments.MaxEditBytes + 1));
            await left.NavigateAsync(left.CurrentPath, huge); left.FocusList(); Dispatcher.UIThread.RunJobs();
            Press(window, Key.F4);
            await WaitUntil(() => left.IsEnabled && launcher.Opened.Count == 2);
            Assert.Equal(huge, launcher.Opened[1]);
            Assert.Empty(window.OwnedWindows);
            Assert.Equal(TextDocuments.MaxEditBytes + 1, new FileInfo(huge).Length);
        }
        finally { await Close(window); }

        var readable = temp.File("left/plain.txt", "visible");
        var (readOnlyWindow, readOnlyPane, _, _) = await Open(temp, provider: new ReadOnlyFiles(new LocalFileSystemProvider()));
        try
        {
            await readOnlyPane.NavigateAsync(readOnlyPane.CurrentPath, readable); readOnlyPane.FocusList(); Dispatcher.UIThread.RunJobs();
            Press(readOnlyWindow, Key.F3);
            await WaitUntil(() => readOnlyWindow.OwnedWindows.OfType<TextViewerWindow>().Any());
            Press(readOnlyWindow.OwnedWindows.Single(), Key.Escape);
            await WaitUntil(() => readOnlyPane.IsEnabled);
            Press(readOnlyWindow, Key.F4);
            await WaitUntil(() => readOnlyPane.IsEnabled && readOnlyWindow.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text?.Contains("cannot edit") == true));
            Assert.Empty(readOnlyWindow.OwnedWindows);
            Assert.Equal("visible", File.ReadAllText(readable));
        }
        finally { await Close(readOnlyWindow); }
    }

    [AvaloniaFact]
    public async Task EditorSavesTheSelectedEncodingAndDoesNotReloadOverEdits()
    {
        using var temp = new ScratchDirectory();
        var path = temp.File("left/page.txt", "ą");
        var (window, left, _, _) = await Open(temp);
        try
        {
            await left.NavigateAsync(left.CurrentPath, path); left.FocusList(); Dispatcher.UIThread.RunJobs();
            Press(window, Key.F4);
            await WaitUntil(() => window.OwnedWindows.OfType<TextEditorWindow>().Any());
            var editor = (TextEditorWindow)window.OwnedWindows.Single();
            var text = editor.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "EditorText");
            text.Text += "x";
            Click(editor, "Reload");
            Assert.EndsWith("x", text.Text);
            Assert.Contains(editor.GetVisualDescendants().OfType<TextBlock>(), block => block.Text?.Contains("discard") == true);
            text.Text = "ą";
            editor.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "Encoding").SelectedItem =
                TextDocuments.Encodings.Single(choice => choice.Id == "windows-1250");
            Press(editor, Key.S, RawInputModifiers.Control);
            await WaitUntil(() => !editor.IsDirty);
            Assert.Equal(TextDocuments.Encode("ą", "windows-1250", "\n"), File.ReadAllBytes(path));
            var document = TextDocuments.Decode(File.ReadAllBytes(path), encodingId: "windows-1250");
            Assert.Equal("ą", document.Text);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task BuiltInEditorSavesThroughARemoteProvider()
    {
        var remote = new MemorySftp();
        remote.AddDirectory("/");
        remote.AddFile("/notes.txt", "hello");
        using var provider = new SftpFileSystemProvider(remote, "box", "Box", "/");
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        try
        {
            var path = provider.NormalizePath("sftp://box/notes.txt");
            var document = await TextDocuments.ReadAsync(provider, path, TextDocuments.MaxEditBytes, false);
            var entry = (await provider.GetEntryAsync(path))!;
            var editor = new TextEditorWindow(owner, entry, provider, document);
            editor.Show();
            Dispatcher.UIThread.RunJobs();
            var text = editor.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "EditorText");
            text.Text = "hello!";
            Press(editor, Key.S, RawInputModifiers.Control);
            await WaitUntil(() => remote.ReadFile("/notes.txt") == "hello!" && !editor.IsDirty);
            Assert.Contains(editor.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Saved.");
            editor.Close();
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task ReloadRereadsTheProviderAndKeepsDirtyOrFailedText()
    {
        var remote = new MemorySftp();
        remote.AddDirectory("/");
        remote.AddFile("/notes.txt", "hello");
        using var provider = new SftpFileSystemProvider(remote, "box", "Box", "/");
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        try
        {
            var path = provider.NormalizePath("/notes.txt");
            var document = await TextDocuments.ReadAsync(provider, path, TextDocuments.MaxEditBytes, false);
            var editor = new TextEditorWindow(owner, (await provider.GetEntryAsync(path))!, provider, document);
            editor.Show();
            Dispatcher.UIThread.RunJobs();
            var text = editor.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "EditorText");
            remote.AddFile("/notes.txt", "changed");
            Click(editor, "Reload");
            await WaitUntil(() => text.Text == "changed");
            Assert.False(editor.IsDirty);
            remote.FailRead = true;
            Click(editor, "Reload");
            await WaitUntil(() => editor.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "The file could not be read."));
            Assert.Equal("changed", text.Text);
            text.Text += "!";
            Click(editor, "Reload");
            Assert.Equal("changed!", text.Text);
            Assert.Contains(editor.GetVisualDescendants().OfType<TextBlock>(), block => block.Text?.Contains("discard") == true);
            text.Text = "changed";
            editor.Close();
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task SecondCopyWaitsAndDeleteCanBeConfirmedWhileItRuns()
    {
        using var temp = new ScratchDirectory();
        var first = temp.File("left/a.txt", "one");
        var second = temp.File("left/b.txt", "two");
        var provider = new GatedTransferProvider(new LocalFileSystemProvider());
        var (window, left, right, _) = await Open(temp, provider: provider);
        try
        {
            await left.NavigateAsync(left.CurrentPath, first); left.FocusList();
            Press(window, Key.F5);
            await WaitUntil(() => window.OwnedWindows.Any());
            Press(window.OwnedWindows.Single(), Key.Enter);
            await WaitUntil(() => provider.Transfers == 1 && left.IsEnabled);
            Assert.False(File.Exists(Path.Combine(right.CurrentPath, "a.txt")));
            await left.NavigateAsync(left.CurrentPath, second);
            Press(window, Key.F5);
            await WaitUntil(() => window.OwnedWindows.Any());
            Press(window.OwnedWindows.Single(), Key.Enter);
            await WaitUntil(() => window.WaitingTransfers == 1 && left.IsEnabled);
            Assert.Equal(1, provider.Transfers);
            Press(window, Key.F8);
            await WaitUntil(() => window.OwnedWindows.Any());
            Assert.Equal("Delete permanently", window.OwnedWindows.Single().Title);
            Press(window.OwnedWindows.Single(), Key.Escape);
            await WaitUntil(() => left.IsEnabled && window.WaitingTransfers == 1);
            Assert.True(File.Exists(first));
            window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoveQueuedTransfer")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => window.WaitingTransfers == 0);
            provider.Release();
            await WaitUntilReady(window, left);
            Assert.Equal("one", File.ReadAllText(Path.Combine(right.CurrentPath, "a.txt")));
            Assert.False(File.Exists(Path.Combine(right.CurrentPath, "b.txt")));
            Assert.Equal(1, provider.Transfers);
        }
        finally { provider.Release(); await Close(window); }
    }

    [AvaloniaFact]
    public async Task FindFilesNavigatesToTheResultAndLeavesEditorSearchAlone()
    {
        using var temp = new ScratchDirectory();
        var nested = temp.File("left/sub/b.txt", "abcd");
        var (window, left, _, _) = await Open(temp);
        try
        {
            left.FocusList();
            Press(window, Key.F7, RawInputModifiers.Alt);
            await WaitUntil(() => window.OwnedWindows.Any(dialog => dialog.Title == "Find files"));
            var opened = window.OwnedWindows.Single();
            Assert.Equal(left.CurrentDisplayPath, opened.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "FindStart").Text);
            Press(opened, Key.Escape);
            await WaitUntil(() => !window.OwnedWindows.Any());
            Press(window, Key.F, RawInputModifiers.Control);
            await WaitUntil(() => window.OwnedWindows.Any(dialog => dialog.Title == "Find files"));
            var dialog = window.OwnedWindows.Single();
            dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "FindPattern").Text = "b.txt";
            Click(dialog, "Search");
            var list = dialog.GetVisualDescendants().OfType<ListBox>().Single(box => box.Name == "FindResults");
            await WaitUntil(() => list.Items.OfType<FindFilesWindow.SearchRow>().Any(row => row.Name == "b.txt"));
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), block => block.Name == "FindStatus" && block.Text == "1 result");
            list.SelectedItem = list.Items.OfType<FindFilesWindow.SearchRow>().Single(row => row.Name == "b.txt");
            list.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));
            await WaitUntil(() => !window.OwnedWindows.Any() && left.CurrentEntry?.Name == "b.txt");
            Assert.True(left.Provider.PathComparer.Equals(left.Provider.NormalizePath(Path.GetDirectoryName(nested)!), left.CurrentPath));
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task SpaceCalculatesAFolderSizeAndCtrlSpaceStillMarks()
    {
        using var temp = new ScratchDirectory();
        var folder = temp.Folder("left/docs");
        temp.File("left/docs/a.txt", "abcd");
        var notes = temp.File("left/notes.txt", "source");
        var (window, left, _, _) = await Open(temp);
        try
        {
            await left.NavigateAsync(left.CurrentPath, folder);
            left.FocusList();
            Press(window, Key.Space);
            await WaitUntil(() => left.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "4 B"));
            await left.NavigateAsync(left.CurrentPath, notes);
            left.FocusList();
            Press(window, Key.Space);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(left.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "6 B");
            Assert.DoesNotContain(left.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Calculating…");
            Press(window, Key.Space, RawInputModifiers.Control);
            await WaitUntil(() => left.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "●"));
        }
        finally { await Close(window); }
    }

    private sealed class GatedTransferProvider(IFileSystemProvider inner) : IFileSystemProvider
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _transfers;
        public int Transfers => _transfers;
        public void Release() => _gate.TrySetResult();
        public string Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public bool SupportsRecycle => inner.SupportsRecycle;
        public StringComparer PathComparer => inner.PathComparer;
        public bool CanHandle(string path) => inner.CanHandle(path);
        public string NormalizePath(string path) => inner.NormalizePath(path);
        public string CombinePath(string directory, string name) => inner.CombinePath(directory, name);
        public bool IsSameOrDescendant(string path, string ancestor) => inner.IsSameOrDescendant(path, ancestor);
        public Task<IReadOnlyList<LocationEntry>> GetLocationsAsync(CancellationToken cancellationToken = default) => inner.GetLocationsAsync(cancellationToken);
        public Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken = default) => inner.ListAsync(path, cancellationToken);
        public Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default) => inner.GetEntryAsync(path, cancellationToken);
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => inner.CreateDirectoryAsync(path, cancellationToken);
        public Task DeleteAsync(string path, DeleteMode mode, CancellationToken cancellationToken = default) => inner.DeleteAsync(path, mode, cancellationToken);
        public Task RemoveEmptyDirectoryAsync(string path, CancellationToken cancellationToken = default) => inner.RemoveEmptyDirectoryAsync(path, cancellationToken);
        public Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default) => inner.CopyFileAsync(sourcePath, destinationPath, overwrite, cancellationToken);
        public Task MoveFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default) => inner.MoveFileAsync(sourcePath, destinationPath, overwrite, cancellationToken);
        public async Task TransferFileAsync(string sourcePath, string destinationPath, bool overwrite, bool move,
            IProgress<long>? bytesCopied, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _transfers);
            await _gate.Task.WaitAsync(cancellationToken);
            await inner.TransferFileAsync(sourcePath, destinationPath, overwrite, move, bytesCopied, cancellationToken);
        }
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

    private sealed class RejectingWriter(IFileSystemProvider inner) : IFileSystemProvider
    {
        public string Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public bool SupportsRecycle => inner.SupportsRecycle;
        public bool CanWriteFileContent => true;
        public StringComparer PathComparer => inner.PathComparer;
        public bool CanHandle(string path) => inner.CanHandle(path);
        public string NormalizePath(string path) => inner.NormalizePath(path);
        public string CombinePath(string directory, string name) => inner.CombinePath(directory, name);
        public bool IsSameOrDescendant(string path, string ancestor) => inner.IsSameOrDescendant(path, ancestor);
        public Task<IReadOnlyList<LocationEntry>> GetLocationsAsync(CancellationToken cancellationToken = default) => inner.GetLocationsAsync(cancellationToken);
        public Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken = default) => inner.ListAsync(path, cancellationToken);
        public Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default) => inner.GetEntryAsync(path, cancellationToken);
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => inner.CreateDirectoryAsync(path, cancellationToken);
        public Task DeleteAsync(string path, DeleteMode mode, CancellationToken cancellationToken = default) => inner.DeleteAsync(path, mode, cancellationToken);
        public Task RemoveEmptyDirectoryAsync(string path, CancellationToken cancellationToken = default) => inner.RemoveEmptyDirectoryAsync(path, cancellationToken);
        public Task CopyFileAsync(string source, string target, bool overwrite, CancellationToken cancellationToken = default) => inner.CopyFileAsync(source, target, overwrite, cancellationToken);
        public Task MoveFileAsync(string source, string target, bool overwrite, CancellationToken cancellationToken = default) => inner.MoveFileAsync(source, target, overwrite, cancellationToken);
        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default) => inner.OpenReadAsync(path, cancellationToken);
        public Task<string?> GetLocalFilePathAsync(string path, CancellationToken cancellationToken = default) => inner.GetLocalFilePathAsync(path, cancellationToken);
        public Task WriteFileAsync(string destinationPath, Stream content, bool overwrite, IProgress<long>? bytesCopied, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("The disk is full."));
    }

    private sealed class ReadOnlyFiles(IFileSystemProvider inner) : IFileSystemProvider
    {
        public string Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public bool SupportsRecycle => inner.SupportsRecycle;
        public bool CanWriteFileContent => false;
        public StringComparer PathComparer => inner.PathComparer;
        public bool CanHandle(string path) => inner.CanHandle(path);
        public string NormalizePath(string path) => inner.NormalizePath(path);
        public string CombinePath(string directory, string name) => inner.CombinePath(directory, name);
        public bool IsSameOrDescendant(string path, string ancestor) => inner.IsSameOrDescendant(path, ancestor);
        public Task<IReadOnlyList<LocationEntry>> GetLocationsAsync(CancellationToken cancellationToken = default) => inner.GetLocationsAsync(cancellationToken);
        public Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken = default) => inner.ListAsync(path, cancellationToken);
        public Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default) => inner.GetEntryAsync(path, cancellationToken);
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => inner.CreateDirectoryAsync(path, cancellationToken);
        public Task DeleteAsync(string path, DeleteMode mode, CancellationToken cancellationToken = default) => inner.DeleteAsync(path, mode, cancellationToken);
        public Task RemoveEmptyDirectoryAsync(string path, CancellationToken cancellationToken = default) => inner.RemoveEmptyDirectoryAsync(path, cancellationToken);
        public Task CopyFileAsync(string source, string target, bool overwrite, CancellationToken cancellationToken = default) => inner.CopyFileAsync(source, target, overwrite, cancellationToken);
        public Task MoveFileAsync(string source, string target, bool overwrite, CancellationToken cancellationToken = default) => inner.MoveFileAsync(source, target, overwrite, cancellationToken);
        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default) => inner.OpenReadAsync(path, cancellationToken);
        public Task<string?> GetLocalFilePathAsync(string path, CancellationToken cancellationToken = default) => inner.GetLocalFilePathAsync(path, cancellationToken);
    }

    private sealed class RecordingTrash : ITrashService
    {
        public bool IsSupported => true;
        public List<string> Paths { get; } = [];
        public Task RecycleAsync(string path, CancellationToken cancellationToken = default) { Paths.Add(path); return Task.CompletedTask; }
    }
}
