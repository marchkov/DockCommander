using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.Automation;
using DockCommander.Core.Files;
using DockCommander.Core.Operations;
using DockCommander.App.Platform;

namespace DockCommander.App;

public sealed class MainWindow : Window
{
    private readonly FileOperationService _operations = new();
    private readonly SettingsStore _settings;
    private readonly IExternalFileLauncher _launcher;
    private readonly TextPreviewService _textPreview = new();
    private readonly StackPanel _transferPanel = new() { IsVisible = false, Spacing = 5, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _transferText = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
    private readonly ProgressBar _transferBar = new() { Minimum = 0, Maximum = 100, Height = 5 };
    private readonly TextBlock _batchText = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _batchBar = new() { Minimum = 0, Maximum = 100, Height = 7 };
    private readonly Button _cancelTransfer = new() { Content = "Cancel · Esc", Name = "CancelTransferButton" };
    private string _editorExecutable = "";
    private readonly FilePane _left;
    private readonly FilePane _right;
    private readonly TextBlock _status = new() { Text = "Starting…", TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly UniformGrid _keys = new() { Rows = 1, Columns = 6, Margin = new Thickness(-3, 0, -3, 0) };
    private readonly Button _lightTheme = new() { Name = "LightThemeButton", Content = UiIcons.Label("sun", "Light") };
    private readonly Button _darkTheme = new() { Name = "DarkThemeButton", Content = UiIcons.Label("moon", "Dark") };
    private FilePane _active;
    private bool _busy;
    private bool _allowClose;
    private bool _saving;
    private CancellationTokenSource? _operationCancellation;
    private double _normalWidth = 1200;
    private double _normalHeight = 760;

    public MainWindow() : this(new SettingsStore(), CreateLocalProvider()) { }

    private static IFileSystemProvider CreateLocalProvider()
    {
        ITrashService trash = OperatingSystem.IsWindows() ? new WindowsTrashService() :
            OperatingSystem.IsLinux() ? new LinuxTrashService() : new UnsupportedTrashService();
        return new LocalFileSystemProvider(trash);
    }

    internal MainWindow(SettingsStore settings, IFileSystemProvider provider, IExternalFileLauncher? launcher = null)
    {
        _settings = settings;
        _launcher = launcher ?? new ExternalFileLauncher();
        Title = "DockCommander";
        ExtendClientAreaToDecorationsHint = true;
        WindowDecorations = WindowDecorations.None;
        ExtendClientAreaTitleBarHeightHint = 56;
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.ico");
        if (System.IO.File.Exists(iconPath)) Icon = new WindowIcon(iconPath);
        Width = _normalWidth; Height = _normalHeight; MinWidth = 900; MinHeight = 450;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _left = new(provider, home, "LEFT"); _right = new(provider, home, "RIGHT"); _active = _left;
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(12, 0, 12, 12) };
        grid.Children.Add(BuildToolbar());
        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*") };
        panes.Children.Add(_left); Grid.SetColumn(_right, 2); panes.Children.Add(_right);
        Grid.SetRow(panes, 1); grid.Children.Add(panes);
        _status.FontSize = 12; _status.Classes.Add("muted");
        var transferHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _transferText.VerticalAlignment = VerticalAlignment.Center;
        transferHeader.Children.Add(_transferText);
        Grid.SetColumn(_cancelTransfer, 1); transferHeader.Children.Add(_cancelTransfer);
        _cancelTransfer.Click += (_, _) =>
        {
            _operationCancellation?.Cancel(); _cancelTransfer.IsEnabled = false;
            _transferText.Text = "Cancelling… Completed files will be kept.";
        };
        _transferPanel.Children.Add(_batchText); _transferPanel.Children.Add(_batchBar);
        _transferPanel.Children.Add(transferHeader); _transferPanel.Children.Add(_transferBar);
        var statusBody = new StackPanel { Spacing = 6 };
        statusBody.Children.Add(_transferPanel); statusBody.Children.Add(_status);
        var status = new Border { Padding = new Thickness(2, 8), Child = statusBody };
        Grid.SetRow(status, 2); grid.Children.Add(status);
        foreach (var (key, label, icon, action) in new (string, string, string, Func<Task>)[]
        {
            ("F3", "View", "view", ViewAsync),
            ("F4", "Edit", "edit", EditAsync),
            ("F5", "Copy", "copy", () => TransferAsync(false)),
            ("F6", "Move", "move", () => TransferAsync(true)),
            ("F7", "New folder", "mkdir", CreateDirectoryAsync),
            ("F8", "Recycle", "trash", () => DeleteAsync(false))
        })
        {
            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            content.Children.Add(UiIcons.Label(icon, label, key == "F8" ? "danger" : null));
            var capText = new TextBlock { Text = key, FontSize = 11, FontWeight = FontWeight.SemiBold }; capText.Classes.Add("muted");
            var cap = new Border { Child = capText, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            cap.Classes.Add("keycap"); Grid.SetColumn(cap, 1); content.Children.Add(cap);
            var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(3, 0), Padding = new Thickness(10, 9) };
            AutomationProperties.SetName(button, $"{key} {label}");
            ToolTip.SetTip(button, key switch
            {
                "F8" => "F8: recycle · Shift+F8: delete permanently",
                "F6" => "F6: move · Shift+F6: rename current entry",
                "F4" => "F4: edit current file · Shift+F4: choose editor",
                _ => $"{key}: {label}"
            });
            button.Click += async (_, _) => await RunAsync(action);
            _keys.Children.Add(button);
        }
        Grid.SetRow(_keys, 3); grid.Children.Add(_keys);
        var frame = new Grid();
        frame.Children.Add(grid);
        AddResizeGrips(frame);
        Content = frame;
        foreach (var pane in new[] { _left, _right })
        {
            pane.Activated += p => { if (!_busy) SetActive(p); };
            pane.NavigateRequested += async (p, path) => await RunAsync(async () =>
            {
                SetActive(p); await p.NavigateAsync(path); SetStatus(p.CurrentPath);
            });
            pane.OpenRequested += async p => await RunAsync(() => OpenAsync(p));
            pane.InteractionError += message => SetStatus($"Error: {message}");
            pane.ExternalDropRequested += async (target, paths, directory) => await RunAsync(async () =>
            {
                SetActive(target);
                var entries = new List<FileEntry>();
                foreach (var path in paths)
                {
                    var entry = await target.Provider.ResolveDroppedFileAsync(path, _operationCancellation!.Token);
                    if (!entries.Any(existing => target.Provider.PathComparer.Equals(existing.FullPath, entry.FullPath))) entries.Add(entry);
                }
                // A folder already includes any explicitly dropped descendants.
                var roots = entries.Where(entry => !entries.Any(parent => parent != entry && parent.IsDirectory &&
                    target.Provider.IsSameOrDescendant(entry.FullPath, parent.FullPath))).ToArray();
                foreach (var entry in roots)
                    if (target.Provider.IsSameOrDescendant(target.Provider.CombinePath(directory, entry.Name), entry.FullPath))
                        throw new IOException("The destination must not be the source or a folder inside it.");
                await TransferAsync(false, target, target, roots, directory,
                    string.Join("\n", roots.Take(5).Select(entry => entry.FullPath)) + (roots.Length > 5 ? "\n…" : ""));
            });
            pane.DropRequested += async (target, payload, directory, move) => await RunAsync(async () =>
            {
                SetActive(payload.Source);
                await TransferAsync(move, payload.Source, target, payload.Entries, directory);
            });
            pane.ActionRequested += async (p, action) =>
            {
                if (_busy || _saving) return;
                SetActive(p);
                await RunAsync(action switch
                {
                    "open" => () => OpenAsync(p), "view" => ViewAsync, "edit" => EditAsync,
                    "copy" => () => TransferAsync(false), "move" => () => TransferAsync(true),
                    "rename" => RenameAsync, "mkdir" => CreateDirectoryAsync, "recycle" => () => DeleteAsync(false),
                    _ => async () => { await p.LoadLocationsAsync(); await p.NavigateAsync(p.CurrentPath); }
                });
            };
        }
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += async (_, _) => await RunAsync(InitializeAsync);
        Closing += OnClosing;
        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal && !_saving)
            {
                _normalWidth = Width; _normalHeight = Height;
            }
        };
        SetActive(_left);
        ApplyTheme("Light");
    }

    private Control BuildToolbar()
    {
        var toolbar = new Grid { Name = "TitleBar", Background = Brushes.Transparent,
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(2, 10, 0, 10) };
        WindowDecorationProperties.SetElementRole(toolbar, WindowDecorationsElementRole.TitleBar);
        toolbar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(toolbar).Properties.IsLeftButtonPressed ||
                e.Source is Visual source && (source is Button || source.GetVisualAncestors().OfType<Button>().Any())) return;
            if (e.ClickCount == 2)
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else BeginMoveDrag(e);
            e.Handled = true;
        };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(UiIcons.Create("logo", "accent", 21));
        brand.Children.Add(new TextBlock { Text = "DockCommander", FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        toolbar.Children.Add(brand);
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        foreach (var (button, name) in new[] { (_lightTheme, "Light"), (_darkTheme, "Dark") })
        {
            button.Classes.Add("flat");
            AutomationProperties.SetName(button, $"Use {name.ToLowerInvariant()} theme");
            ToolTip.SetTip(button, $"{name} theme · Ctrl+Shift+T to toggle");
            button.Click += (_, _) => { ApplyTheme(name); _active.FocusList(); };
            choices.Children.Add(button);
        }
        var switcher = new Border { Child = choices }; switcher.Classes.Add("theme-switch");
        WindowDecorationProperties.SetElementRole(switcher, WindowDecorationsElementRole.User);
        Grid.SetColumn(switcher, 1); toolbar.Children.Add(switcher);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2,
            Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var minimize = CaptionButton("MinimizeButton", "minimize", "Minimize", () => WindowState = WindowState.Minimized);
        var maximize = CaptionButton("MaximizeButton", "maximize", "Maximize", () =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        var close = CaptionButton("CloseButton", "close", "Close", Close);
        close.Classes.Add("caption-close");
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            var maximized = WindowState == WindowState.Maximized;
            maximize.Content = UiIcons.Create(maximized ? "restore" : "maximize", size: 14);
            ToolTip.SetTip(maximize, maximized ? "Restore" : "Maximize");
            AutomationProperties.SetName(maximize, maximized ? "Restore" : "Maximize");
        };
        controls.Children.Add(minimize); controls.Children.Add(maximize); controls.Children.Add(close);
        Grid.SetColumn(controls, 2); toolbar.Children.Add(controls);
        return toolbar;
    }

    private static Button CaptionButton(string name, string icon, string label, Action action)
    {
        var button = new Button { Name = name, Content = UiIcons.Create(icon, size: 14), Width = 38, Height = 34 };
        button.Classes.Add("flat");
        WindowDecorationProperties.SetElementRole(button, WindowDecorationsElementRole.User);
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
        button.Click += (_, _) => action();
        return button;
    }

    private void AddResizeGrips(Grid frame)
    {
        foreach (var (horizontal, vertical, role, cursor) in new[]
        {
            (HorizontalAlignment.Left, VerticalAlignment.Stretch, WindowDecorationsElementRole.ResizeW, StandardCursorType.SizeWestEast),
            (HorizontalAlignment.Right, VerticalAlignment.Stretch, WindowDecorationsElementRole.ResizeE, StandardCursorType.SizeWestEast),
            (HorizontalAlignment.Stretch, VerticalAlignment.Top, WindowDecorationsElementRole.ResizeN, StandardCursorType.SizeNorthSouth),
            (HorizontalAlignment.Stretch, VerticalAlignment.Bottom, WindowDecorationsElementRole.ResizeS, StandardCursorType.SizeNorthSouth),
            (HorizontalAlignment.Left, VerticalAlignment.Top, WindowDecorationsElementRole.ResizeNW, StandardCursorType.TopLeftCorner),
            (HorizontalAlignment.Right, VerticalAlignment.Top, WindowDecorationsElementRole.ResizeNE, StandardCursorType.TopRightCorner),
            (HorizontalAlignment.Left, VerticalAlignment.Bottom, WindowDecorationsElementRole.ResizeSW, StandardCursorType.BottomLeftCorner),
            (HorizontalAlignment.Right, VerticalAlignment.Bottom, WindowDecorationsElementRole.ResizeSE, StandardCursorType.BottomRightCorner)
        })
        {
            var grip = new Border { Background = Brushes.Transparent, HorizontalAlignment = horizontal,
                VerticalAlignment = vertical, Cursor = new Cursor(cursor) };
            if (horizontal != HorizontalAlignment.Stretch) grip.Width = 5;
            if (vertical != VerticalAlignment.Stretch) grip.Height = 5;
            WindowDecorationProperties.SetElementRole(grip, role);
            grip.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed || WindowState != WindowState.Normal) return;
                var edge = role switch
                {
                    WindowDecorationsElementRole.ResizeW => WindowEdge.West,
                    WindowDecorationsElementRole.ResizeE => WindowEdge.East,
                    WindowDecorationsElementRole.ResizeN => WindowEdge.North,
                    WindowDecorationsElementRole.ResizeS => WindowEdge.South,
                    WindowDecorationsElementRole.ResizeNW => WindowEdge.NorthWest,
                    WindowDecorationsElementRole.ResizeNE => WindowEdge.NorthEast,
                    WindowDecorationsElementRole.ResizeSW => WindowEdge.SouthWest,
                    _ => WindowEdge.SouthEast
                };
                BeginResizeDrag(edge, e);
                e.Handled = true;
            };
            PropertyChanged += (_, e) =>
            {
                if (e.Property == WindowStateProperty) grip.IsVisible = WindowState == WindowState.Normal;
            };
            frame.Children.Add(grip);
        }
    }

    private void ApplyTheme(string? theme)
    {
        var dark = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase);
        RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        _lightTheme.Classes.Set("chosen", !dark); _darkTheme.Classes.Set("chosen", dark);
    }

    private async Task InitializeAsync()
    {
        var settings = await _settings.LoadAsync();
        _editorExecutable = settings.EditorExecutable ?? "";
        ApplyTheme(settings.Theme);
        Width = double.IsFinite(settings.Width) ? Math.Clamp(settings.Width, MinWidth, 4000) : 1200;
        Height = double.IsFinite(settings.Height) ? Math.Clamp(settings.Height, MinHeight, 2400) : 760;
        _normalWidth = Width; _normalHeight = Height;
        if (settings.Maximized) WindowState = WindowState.Maximized;
        var warnings = new List<string>();
        if (_settings.LoadWarning is { } warning) warnings.Add(warning);
        foreach (var (pane, path) in new[] { (_left, settings.LeftPath), (_right, settings.RightPath) })
        {
            try { await pane.LoadLocationsAsync(); }
            catch (Exception ex) { warnings.Add(ex.Message); }
            try { await pane.NavigateAsync(path); }
            catch (Exception ex)
            {
                warnings.Add($"Could not open {path}: {ex.Message}");
                try { await pane.NavigateAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)); }
                catch (Exception fallback) { warnings.Add(fallback.Message); }
            }
        }
        SetActive(settings.LeftActive ? _left : _right);
        SetStatus(warnings.Count == 0 ? "Ready · Insert marks files · F8 recycle · Shift+F8 delete permanently" : string.Join(" | ", warnings));
    }

    private void SetActive(FilePane pane)
    {
        _active = pane; _left.SetActive(pane == _left); _right.SetActive(pane == _right);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _saving) return;
        _busy = true;
        _operationCancellation = new();
        _left.IsEnabled = _right.IsEnabled = _keys.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) { SetStatus("Cancelled. Already completed changes are kept."); }
        catch (Exception ex) { SetStatus($"Error: {ex.Message}"); }
        finally
        {
            _left.ResetPathText(); _right.ResetPathText();
            _operationCancellation.Dispose(); _operationCancellation = null;
            _left.IsEnabled = _right.IsEnabled = _keys.IsEnabled = true;
            _busy = false;
            _active.FocusList();
            Dispatcher.UIThread.Post(() => { if (!_busy && !_saving) _active.FocusList(); }, DispatcherPriority.Background);
        }
    }

    private async Task OpenAsync(FilePane pane)
    {
        SetActive(pane);
        if (pane.CurrentEntry is not { } entry) return;
        if (!entry.IsDirectory)
        {
            var localPath = await RequireLocalFileAsync(pane, entry);
            await _launcher.OpenAsync(localPath, _operationCancellation!.Token);
            SetStatus($"Opened: {entry.Name}");
            return;
        }
        var previous = pane.CurrentPath;
        await pane.NavigateAsync(entry.FullPath, entry.Name == ".." ? previous : null);
        SetStatus(pane.CurrentPath);
    }

    private async Task<string> RequireLocalFileAsync(FilePane pane, FileEntry entry) =>
        await pane.Provider.GetLocalFilePathAsync(entry.FullPath, _operationCancellation!.Token)
        ?? throw new NotSupportedException("This provider cannot expose a local file for an external application.");

    private async Task ViewAsync()
    {
        if (_active.CurrentEntry is not { IsDirectory: false } entry)
        { SetStatus("Select a file to view."); return; }
        SetStatus($"Reading: {entry.Name} · Esc to cancel");
        var preview = await _textPreview.ReadAsync(_active.Provider, entry.FullPath, _operationCancellation!.Token);
        await new TextViewerWindow(this, entry, preview).ShowDialog(this);
        SetStatus($"Viewed: {entry.Name}");
    }

    private async Task EditAsync()
    {
        if (_active.CurrentEntry is not { IsDirectory: false } entry)
        { SetStatus("Select a file to edit."); return; }
        var localPath = await RequireLocalFileAsync(_active, entry);
        await _launcher.EditAsync(localPath, _editorExecutable, _operationCancellation!.Token);
        SetStatus($"Opened in editor: {entry.Name} · Ctrl+R refreshes after saving");
    }

    private async Task ChooseEditorAsync()
    {
        var editor = await Dialogs.PromptAsync(this, "External editor",
            "Enter the editor executable path or command name, without arguments or surrounding quotes.\n\n" +
            "Leave empty for the platform default (Notepad on Windows, TextEdit on macOS).",
            _editorExecutable, allowEmpty: true);
        if (editor is null) return;
        _editorExecutable = editor.Trim();
        SetStatus(_editorExecutable.Length == 0 ? "Using the platform default editor." : $"Editor: {_editorExecutable}");
    }

    private async Task RenameAsync()
    {
        var pane = _active;
        if (pane.CurrentEntry is not { Name: not ".." } entry)
        { SetStatus("Select a file or folder to rename."); return; }
        var name = await Dialogs.PromptAsync(this, "Rename", $"Rename the current entry:\n{entry.Name}\n\nNew name (including extension):", entry.Name);
        if (name is null) return;
        var newPath = await _operations.RenameAsync(pane.Provider, entry, name, _operationCancellation!.Token);
        await pane.NavigateAsync(pane.CurrentPath, newPath, entry.FullPath);
        var message = $"Renamed: {entry.Name} → {name}";
        var opposite = pane == _left ? _right : _left;
        try { await opposite.NavigateAsync(opposite.CurrentPath); }
        catch (Exception ex) { message += $" Other pane could not refresh: {ex.Message}"; }
        SetStatus(message);
    }

    private async Task TransferAsync(bool move, FilePane? sourcePane = null, FilePane? targetPane = null,
        IReadOnlyList<FileEntry>? draggedEntries = null, string? targetDirectory = null, string? sourceDescription = null)
    {
        var source = sourcePane ?? _active; var destination = targetPane ?? (source == _left ? _right : _left);
        var entries = draggedEntries ?? source.OperationEntries;
        var destinationDirectory = targetDirectory ?? destination.CurrentPath;
        if (entries.Count == 0) { SetStatus("Select a file or folder first."); return; }
        if (!await Dialogs.ConfirmAsync(this, move ? "Move" : "Copy",
                $"{(move ? "Move" : "Copy")} {Describe(entries)}\n\nFrom: {sourceDescription ?? source.CurrentPath}\nTo: {destinationDirectory}",
                move ? "Move" : "Copy")) return;
        _transferPanel.IsVisible = true; _cancelTransfer.IsEnabled = true;
        _transferText.Text = move ? "Preparing move…" : "Preparing copy…";
        _transferBar.IsIndeterminate = true;
        TransferProgress? latest = null;
        BatchTransferProgress? batch = null;
        _batchText.Text = "Scanning files…"; _batchBar.IsIndeterminate = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            if (_operationCancellation!.IsCancellationRequested)
            {
                _transferText.Text = "Cancelling… Completed files will be kept.";
                _cancelTransfer.IsEnabled = false; return;
            }
            var total = Volatile.Read(ref batch);
            if (total is not null)
            {
                _batchBar.IsIndeterminate = total.IsScanning;
                var percent = total.TotalBytes is > 0 ? 100d * total.ProcessedBytes / total.TotalBytes.Value :
                    total.TotalFiles > 0 ? 100d * total.ProcessedFiles / total.TotalFiles : 0;
                _batchBar.Value = total.IsCompleted ? 100 : Math.Clamp(percent, 0, 99.9);
                _batchText.Text = total.IsScanning ? $"Scanning: {total.Path}" :
                    $"Overall: {total.ProcessedFiles} / {total.TotalFiles} files · skipped: {total.SkippedFiles}" +
                    (total.TotalBytes is long bytes ? $" · {total.ProcessedBytes / 1048576d:0.0} / {bytes / 1048576d:0.0} MiB processed" : "");
            }
            var value = Volatile.Read(ref latest);
            if (value is null) return;
            _transferBar.IsIndeterminate = value.FileSize is null or <= 0;
            _transferBar.Value = value.FileSize is > 0 ? Math.Clamp(100d * value.BytesTransferred / value.FileSize.Value, 0, 100) : 0;
            _transferText.Text = $"{(move ? "Moving" : "Copying")}: {value.Path} · {value.BytesTransferred / 1048576d:0.0} MiB" +
                (value.FileSize is long size ? $" / {size / 1048576d:0.0} MiB (current file)" : "");
            ToolTip.SetTip(_transferText, value.Path);
        };
        timer.Start();
        try
        {
        await WithRefreshAsync(async () =>
        {
            var result = await _operations.TransferAsync(source.Provider, destination.Provider, entries, destinationDirectory,
                move, conflict => Dialogs.ConflictAsync(this, conflict), Progress(), _operationCancellation!.Token,
                new InlineProgress<TransferProgress>(value => Volatile.Write(ref latest, value)),
                new InlineProgress<BatchTransferProgress>(value => Volatile.Write(ref batch, value)));
            return $"Completed: {result.Completed}; skipped or partially transferred: {result.Skipped}.";
        });
        }
        finally { timer.Stop(); _transferPanel.IsVisible = false; }
    }

    private async Task CreateDirectoryAsync()
    {
        var pane = _active;
        var name = await Dialogs.PromptAsync(this, "Create directory", $"In: {pane.CurrentPath}\n\nDirectory name:");
        if (name is null) return;
        await _operations.CreateDirectoryAsync(pane.Provider, pane.CurrentPath, name, _operationCancellation!.Token);
        await pane.NavigateAsync(pane.CurrentPath, pane.Provider.CombinePath(pane.CurrentPath, name));
        SetStatus($"Created: {name}");
    }

    private async Task DeleteAsync(bool permanent)
    {
        var pane = _active; var entries = pane.OperationEntries;
        if (entries.Count == 0) { SetStatus("Select a file or folder first."); return; }
        if (!permanent && !pane.Provider.SupportsRecycle)
            throw new NotSupportedException("The recycle bin is unavailable. Nothing was deleted.");
        if (!await Dialogs.ConfirmAsync(this, permanent ? "Delete permanently" : "Move to recycle bin",
                $"{(permanent ? "Permanently delete" : "Recycle")} {Describe(entries, listNames: true)}\n\n" +
                (permanent ? "This bypasses the recycle bin and cannot be undone here." : "You can restore these items from the system recycle bin."),
                permanent ? "Delete permanently" : "Recycle")) return;
        await WithRefreshAsync(async () =>
        {
            await _operations.DeleteAsync(pane.Provider, entries, permanent ? DeleteMode.Permanent : DeleteMode.Recycle,
                Progress(), _operationCancellation!.Token);
            return permanent ? "Permanently deleted." : "Moved to recycle bin.";
        });
    }

    private async Task WithRefreshAsync(Func<Task<string>> operation)
    {
        string message;
        try { message = await operation(); }
        catch (OperationCanceledException ex)
        {
            message = ex.Message.Contains("Already deleted:", StringComparison.Ordinal)
                ? ex.Message : "Cancelled. Already completed changes are kept.";
        }
        catch (Exception ex) { message = $"Error: {ex.Message} Already completed changes are kept."; }
        foreach (var pane in new[] { _left, _right })
        {
            try { await pane.NavigateAsync(pane.CurrentPath); }
            catch (Exception ex) { message += $" Refresh failed: {ex.Message}"; }
        }
        SetStatus(message);
    }

    private IProgress<string> Progress() => new Progress<string>(text => { if (_busy) SetStatus(text + " · Esc cancels after the current file"); });
    private static string Describe(IReadOnlyList<FileEntry> entries, bool listNames = false)
    {
        if (entries.Count == 1) return $"“{entries[0].Name}”?";
        if (!listNames) return $"{entries.Count} selected entries?";
        var names = string.Join("\n", entries.Take(8).Select(entry => entry.Name));
        var extra = entries.Count > 8 ? $"\n… and {entries.Count - 8} more" : "";
        return $"{entries.Count} selected entries?\n\n{names}{extra}";
    }
    private void SetStatus(string text)
    {
        _status.Text = text; ToolTip.SetTip(_status, text);
        _status.Classes.Set("error", text.StartsWith("Error:", StringComparison.Ordinal));
    }

    private async Task SelectByMaskAsync(bool marked)
    {
        var mask = await Dialogs.PromptAsync(this, marked ? "Select files" : "Unselect files",
            "File mask: * matches any text, ? matches one character.\nSeparate masks with ; (example: *.txt;*.md). Folders are unchanged.", "*");
        if (mask is not null) _active.MarkFiles(mask, marked);
    }

    private async Task ChooseLocationAsync(FilePane pane)
    {
        var locations = await pane.Provider.GetLocationsAsync();
        if (locations.Count == 0) { SetStatus("No locations available."); return; }
        var selected = locations.Select((location, index) => (location, index))
            .Where(x => pane.Provider.IsSameOrDescendant(pane.CurrentPath, x.location.RootPath))
            .OrderByDescending(x => x.location.RootPath.Length).Select(x => x.index).DefaultIfEmpty(0).First();
        var location = await Dialogs.ChooseLocationAsync(this, pane == _left ? "Left panel location" : "Right panel location", locations, selected);
        if (location is null) return;
        await pane.NavigateAsync(location.RootPath);
        SetActive(pane);
        await pane.LoadLocationsAsync();
        SetStatus(pane.CurrentPath);
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_left.IsDragging || _right.IsDragging) return;
        if (_busy)
        {
            if (e.Key == Key.Escape) _operationCancellation?.Cancel();
            e.Handled = true; return;
        }
        if (e.Key == Key.T && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true; ApplyTheme(RequestedThemeVariant == ThemeVariant.Dark ? "Light" : "Dark"); return;
        }
        // Text fields and buttons handle their own Enter key.
        if (e.Key == Key.Enter && e.Source is Visual visual &&
            (visual is TextBox or Button ||
             visual.GetVisualAncestors().Any(parent => parent is TextBox or Button))) return;
        if (e.Key == Key.L && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true; _active.FocusPath(); return;
        }
        if (e.Key is Key.Add or Key.Subtract or Key.Multiply &&
            (e.KeyModifiers != KeyModifiers.None || e.Source is Visual input &&
             (input is TextBox || input.GetVisualAncestors().OfType<TextBox>().Any()))) return;
        Func<Task>? action = e.Key switch
        {
            Key.F1 when e.KeyModifiers == KeyModifiers.Alt => () => ChooseLocationAsync(_left),
            Key.F2 when e.KeyModifiers == KeyModifiers.Alt => () => ChooseLocationAsync(_right),
            Key.Add => () => SelectByMaskAsync(true),
            Key.Subtract => () => SelectByMaskAsync(false),
            Key.Multiply => () => { _active.InvertFileMarks(); return Task.CompletedTask; },
            Key.F3 => ViewAsync,
            Key.F4 when e.KeyModifiers.HasFlag(KeyModifiers.Shift) => ChooseEditorAsync,
            Key.F4 => EditAsync,
            Key.F5 => () => TransferAsync(false),
            Key.F6 when e.KeyModifiers.HasFlag(KeyModifiers.Shift) => RenameAsync,
            Key.F6 => () => TransferAsync(true),
            Key.F7 => CreateDirectoryAsync,
            Key.F8 => () => DeleteAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift)),
            Key.Enter => () => OpenAsync(_active),
            Key.R when e.KeyModifiers.HasFlag(KeyModifiers.Control) => async () =>
            {
                await _active.LoadLocationsAsync(); await _active.NavigateAsync(_active.CurrentPath); SetStatus(_active.CurrentPath);
            },
            _ => null
        };
        if (e.Key == Key.Tab)
        {
            e.Handled = true; SetActive(_active == _left ? _right : _left); _active.FocusList(); return;
        }
        if (action is null) return;
        e.Handled = true;
        await RunAsync(action);
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_busy) { SetStatus("An operation is in progress. Press Esc to cancel, then close the window."); return; }
        if (_saving) return;
        _saving = true;
        try
        {
            await _settings.SaveAsync(new(_left.CurrentPath, _right.CurrentPath, _normalWidth, _normalHeight,
                _active == _left, WindowState == WindowState.Maximized,
                RequestedThemeVariant == ThemeVariant.Dark ? "Dark" : "Light", _editorExecutable));
            _allowClose = true; Close();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save settings: {ex.Message}");
            if (await Dialogs.ConfirmAsync(this, "Settings could not be saved", ex.Message + "\n\nClose without saving settings?", "Close"))
            {
                _allowClose = true; Close();
            }
        }
        finally { _saving = false; }
    }
}
