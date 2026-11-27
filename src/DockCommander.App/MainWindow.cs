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
using DockCommander.Core.Connections;
using DockCommander.Core.Files;
using DockCommander.Core.Operations;
using DockCommander.App.Platform;

namespace DockCommander.App;

public sealed class MainWindow : Window
{
    private readonly FileOperationService _operations = new();
    private readonly TransferQueue _transfers;
    private readonly SettingsStore _settings;
    private readonly ConnectionStore _connections;
    private readonly SecretStore _secrets;
    private readonly Dictionary<string, IFileSystemProvider> _sessions = new(StringComparer.Ordinal);
    private readonly List<OpenSessionChip> _open = [];
    private readonly List<IFileSystemProvider> _live = [];
    private readonly Dictionary<string, string> _trusted = new(StringComparer.Ordinal);
    private readonly object _trustLock = new();
    private List<ConnectionProfile> _profiles = [];
    private readonly CancellationTokenSource _windowLifetime = new();
    private readonly IExternalFileLauncher _launcher;
    private readonly ISftpConnector _sftp;
    private readonly IFtpConnector _ftp;
    private readonly IWebDavConnector _webDav;
    private readonly TextPreviewService _textPreview = new();
    private readonly StackPanel _transferPanel = new() { IsVisible = false, Spacing = 5, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _queueHost = new() { Spacing = 4 };
    private readonly TextBlock _jobText = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _transferText = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
    private DispatcherTimer? _transferTimer;
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
    private CancellationTokenSource? _sizeCancellation;
    private readonly FolderSizeGate _folderSizes = new();
    private int _sizeGeneration;
    private FilePane? _sizingPane;
    private string? _sizingPath;
    private double _normalWidth = 1200;
    private double _normalHeight = 760;

    public MainWindow() : this(new SettingsStore(), CreateLocalProvider()) { }

    private static IFileSystemProvider CreateLocalProvider()
    {
        ITrashService trash = OperatingSystem.IsWindows() ? new WindowsTrashService() :
            OperatingSystem.IsLinux() ? new LinuxTrashService() :
            OperatingSystem.IsMacOS() ? new MacTrashService() : new UnsupportedTrashService();
        return new LocalFileSystemProvider(trash);
    }

    internal MainWindow(SettingsStore settings, IFileSystemProvider provider, IExternalFileLauncher? launcher = null, ISftpConnector? sftp = null, IFtpConnector? ftp = null, IWebDavConnector? webDav = null)
    {
        _settings = settings;
        _connections = ConnectionStore.Beside(settings);
        _secrets = SecretStore.Beside(settings);
        _launcher = launcher ?? new ExternalFileLauncher();
        _sftp = sftp ?? new SshNetSftpConnector();
        _ftp = ftp ?? new FluentFtpConnector();
        _webDav = webDav ?? new WebDavConnector();
        _transfers = new(_operations);
        _transfers.Changed += () => Dispatcher.UIThread.Post(UpdateQueuePanel);
        _transfers.JobCompleted = FinishTransferAsync;
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
        _cancelTransfer.Click += (_, _) => CancelRunningTransfer();
        _transferPanel.Children.Add(_queueHost);
        _transferPanel.Children.Add(_jobText);
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
                "F8" => "F8: recycle, or delete permanently when this pane has no recycle bin · Shift+F8: delete permanently",
                "F6" => "F6: move · Shift+F6: rename current entry",
                "F4" => "F4: edit text, or open another file with its system application · Shift+F4: new file · Ctrl+Shift+F4: choose external editor",
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
                SetActive(p); await p.NavigateAsync(path); SetStatus(p.CurrentDisplayPath);
            });
            pane.OpenRequested += async p => await RunAsync(() => OpenAsync(p));
            pane.InteractionError += message => SetStatus($"Error: {message}");
            pane.AddConnectionRequested += () => _ = EditConnectionAsync(null);
            pane.EditConnectionRequested += profile => _ = EditConnectionAsync(profile);
            pane.ConnectRequested += profile => _ = ConnectAsync(pane, profile);
            pane.ReturnedToLocal += p => _ = RunAsync(async () =>
            {
                await p.LoadLocationsAsync();
                await p.NavigateAsync(p.SavedPath);
                SetStatus(p.CurrentDisplayPath);
            });
            pane.RemoteRestored += p => _ = RunAsync(async () =>
            {
                await p.LoadLocationsAsync();
                await p.NavigateAsync(p.SessionPath);
                SetStatus(p.CurrentDisplayPath);
            });
            pane.RemoteWithoutSession += p =>
            {
                var live = _open.Where(session => session.Link == SessionLink.Connected && session.Provider is not null).ToArray();
                if (live.Length == 1)
                    _ = AttachSharedAsync(p, live[0].Provider!, PathForPane(p, live[0].Provider!, Profile(live[0].Id)));
                else
                    p.ShowConnectionList();
            };
            pane.SessionChosen += (p, id) =>
            {
                if (_sessions.ContainsKey(id)) _ = SwitchSessionAsync(p, id);
                else _ = ReconnectSessionAsync(p, id);
            };
            pane.SessionEditRequested += (pane, id) =>
            {
                if (Profile(id) is { } profile) _ = EditConnectionAsync(profile);
            };
            pane.SessionReconnectRequested += (p, id) => _ = ReconnectSessionAsync(p, id);
            pane.SessionDisconnectRequested += (_, id) => DisconnectSession(id);
            pane.SessionForgetRequested += (source, id) => _ = ForgetSignInAsync(id);
            pane.SessionCloseRequested += (_, id) => CloseSession(id);
            if (OperatingSystem.IsWindows())
                pane.SystemMenuRequested += (p, paths, point) => Dispatcher.UIThread.Post(async () =>
                {
                    if (!_transfers.IsIdle)
                    {
                        SetStatus("System menu is unavailable while transfers are still queued.");
                        return;
                    }
                    await RunAsync(async () =>
                    {
                        SetActive(p);
                        try
                        {
                            if (OperatingSystem.IsWindows())
                                WindowsShellMenu.Show(TryGetPlatformHandle()?.Handle ?? 0, p.CurrentPath, paths, point.X, point.Y,
                                    ActualThemeVariant == ThemeVariant.Dark);
                        }
                        finally
                        {
                            await _left.RefreshAsync();
                            await _right.RefreshAsync();
                        }
                    });
                });
            pane.ExternalDropRequested += async (target, paths, directory) => await RunAsync(async () =>
            {
                SetActive(target);
                var source = target.Provider.SupportsLocalFileDrop ? target.Provider : target.LocalProvider;
                var entries = new List<FileEntry>();
                foreach (var path in paths)
                {
                    var entry = await source.ResolveDroppedFileAsync(path, _operationCancellation!.Token);
                    if (!entries.Any(existing => source.PathComparer.Equals(existing.FullPath, entry.FullPath))) entries.Add(entry);
                }
                // A folder already includes any explicitly dropped descendants.
                var roots = entries.Where(entry => !entries.Any(parent => parent != entry && parent.IsDirectory &&
                    source.IsSameOrDescendant(entry.FullPath, parent.FullPath))).ToArray();
                foreach (var entry in roots)
                    if (source.CanHandle(directory) && source.IsSameOrDescendant(source.CombinePath(directory, entry.Name), entry.FullPath))
                        throw new IOException("The destination must not be the source or a folder inside it.");
                await TransferAsync(false, target, target, roots, directory,
                    string.Join("\n", roots.Take(5).Select(entry => entry.FullPath)) + (roots.Length > 5 ? "\n…" : ""),
                    source);
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
                    "external" => EditExternalAsync, "chooseeditor" => ChooseEditorAsync, "newfile" => CreateFileAsync,
                    "copy" => () => TransferAsync(false), "move" => () => TransferAsync(true),
                    "rename" => RenameAsync, "mkdir" => CreateDirectoryAsync, "recycle" => () => DeleteAsync(false),
                    _ => async () => { await p.LoadLocationsAsync(); await p.NavigateAsync(p.CurrentPath); }
                });
            };
        }
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += async (_, _) => await RunAsync(InitializeAsync);
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _windowLifetime.Cancel();
            _windowLifetime.Dispose();
            _ = _transfers.DisposeAsync().AsTask();
        };
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
        _profiles = (await _connections.LoadAsync()).ToList();
        lock (_trustLock)
        {
            foreach (var profile in _profiles)
                if (!string.IsNullOrWhiteSpace(profile.HostFingerprint))
                    _trusted[profile.Id] = profile.HostFingerprint;
        }
        if (_connections.LoadWarning is { } connectionWarning) warnings.Add(connectionWarning);
        PublishConnections();
        foreach (var (pane, path) in new[] { (_left, settings.LeftPath), (_right, settings.RightPath) })
        {
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
        // A disconnected drive may take seconds to answer IsReady. Open the saved
        // folders first and discover locations independently, once for both panes.
        _ = LoadStartupLocationsAsync(_windowLifetime.Token);
    }

    private async Task LoadStartupLocationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var locations = await _left.LocalProvider.GetLocationsAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(_left.Provider, _left.LocalProvider)) _left.SetLocations(locations);
            if (ReferenceEquals(_right.Provider, _right.LocalProvider)) _right.SetLocations(locations);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested && !_busy && _transfers.IsIdle)
                SetStatus($"Could not load locations: {ex.Message}");
        }
    }

    private async Task EditConnectionAsync(ConnectionProfile? existing)
    {
        var result = await Dialogs.EditConnectionAsync(this, existing);
        if (result is null) return;
        if (result.Removed)
        {
            if (existing is not null)
            {
                CloseSession(existing.Id);
                _profiles.RemoveAll(profile => profile.Id == existing.Id);
                lock (_trustLock) _trusted.Remove(existing.Id);
            }
        }
        else if (result.Profile is { } profile)
        {
            var index = _profiles.FindIndex(item => item.Id == profile.Id);
            if (index >= 0) _profiles[index] = profile;
            else _profiles.Add(profile);
            lock (_trustLock)
            {
                if (string.IsNullOrWhiteSpace(profile.HostFingerprint)) _trusted.Remove(profile.Id);
                else _trusted[profile.Id] = profile.HostFingerprint;
            }
        }
        PublishConnections();
        try
        {
            await _connections.SaveAsync(_profiles);
            if (result.Removed && existing is not null) await _secrets.DeleteAsync(existing.Id);
            var savedName = result.Profile?.Name ?? existing?.Name;
            SetStatus(result.Removed ? $"Removed connection: {savedName}" : $"Saved connection: {savedName}");
        }
        catch (Exception ex) { SetStatus($"Could not save connections: {ex.Message}"); }
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
        catch (Exception ex)
        {
            if (IsConnectionClosed(ex))
            {
                NoteConnectionClosed();
                SetStatus($"Disconnected: {ex.Message}");
            }
            else SetStatus($"Error: {ex.Message}");
        }
        finally
        {
            _left.ResetPathText(); _right.ResetPathText();
            _operationCancellation.Dispose(); _operationCancellation = null;
            _left.IsEnabled = _right.IsEnabled = _keys.IsEnabled = true;
            _busy = false;
            if (_transfers.IsIdle) _active.FocusList();
            Dispatcher.UIThread.Post(() =>
            {
                if (!_busy && !_saving && _transfers.IsIdle && !OwnedWindows.Any()) _active.FocusList();
            }, DispatcherPriority.Background);
        }
    }

    private static bool IsFileCommand(KeyEventArgs e) => e.Key switch
    {
        Key.F3 or Key.F5 or Key.F6 or Key.F7 or Key.F8 or Key.Enter or Key.Add or Key.Subtract or Key.Multiply => true,
        Key.F4 => !(e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift)),
        Key.F1 or Key.F2 => e.KeyModifiers == KeyModifiers.Alt,
        Key.R => e.KeyModifiers.HasFlag(KeyModifiers.Control),
        _ => false
    };

    private bool RemoteBlocksFiles(FilePane? pane = null)
    {
        if ((pane ?? _active).IsRemote)
        {
            SetStatus("This pane is showing connections. Switch it to Local for files.");
            return true;
        }
        return false;
    }

    private async Task OpenAsync(FilePane pane)
    {
        if (RemoteBlocksFiles(pane)) return;
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
        SetStatus(pane.CurrentDisplayPath);
    }

    private async Task ConnectAsync(FilePane pane, ConnectionProfile profile)
    {
        if (_sessions.TryGetValue(profile.Id, out var open))
        {
            await AttachSharedAsync(pane, open, PathForPane(pane, open, profile));
            return;
        }
        if (profile.Protocol == ConnectionProtocols.Sftp)
        {
            if (profile.Authentication == AuthenticationKinds.Anonymous)
            {
                SetStatus("SFTP does not support anonymous authentication.");
                return;
            }
            if (string.IsNullOrWhiteSpace(profile.Username))
            {
                SetStatus("Enter a user name for this SFTP connection.");
                return;
            }
            await ConnectSignedInAsync(pane, profile, (login, token) =>
                _sftp.ConnectAsync(profile, login, fingerprint => DecideTrust(profile, fingerprint, false), token));
            return;
        }
        if (profile.Protocol is ConnectionProtocols.Ftp or ConnectionProtocols.Ftps)
        {
            if (profile.Authentication == AuthenticationKinds.PrivateKey)
            {
                SetStatus("FTP does not support private key authentication.");
                return;
            }
            if (profile.Authentication != AuthenticationKinds.Anonymous && string.IsNullOrWhiteSpace(profile.Username))
            {
                SetStatus("Enter a user name for this FTP connection.");
                return;
            }
            if (profile.Authentication == AuthenticationKinds.Anonymous)
            {
                await OpenSession(pane, profile, token => _ftp.ConnectAsync(profile,
                    new SftpCredentials(AuthenticationKinds.Anonymous, "", "", ""),
                    (fingerprint, systemTrusted) => DecideTrust(profile, fingerprint, systemTrusted), token));
                return;
            }
            await ConnectSignedInAsync(pane, profile, (login, token) =>
                _ftp.ConnectAsync(profile, login, (fingerprint, systemTrusted) => DecideTrust(profile, fingerprint, systemTrusted), token));
            return;
        }
        if (profile.Protocol == ConnectionProtocols.WebDav)
        {
            if (profile.Authentication == AuthenticationKinds.PrivateKey)
            {
                SetStatus("WebDAV does not support private key authentication.");
                return;
            }
            if (profile.Authentication != AuthenticationKinds.Anonymous && string.IsNullOrWhiteSpace(profile.Username))
            {
                SetStatus("Enter a user name for this WebDAV connection.");
                return;
            }
            if (profile.Authentication == AuthenticationKinds.Anonymous)
            {
                await OpenSession(pane, profile, token => _webDav.ConnectAsync(profile,
                    new SftpCredentials(AuthenticationKinds.Anonymous, "", "", ""),
                    (fingerprint, systemTrusted) => DecideTrust(profile, fingerprint, systemTrusted), token));
                return;
            }
            await ConnectSignedInAsync(pane, profile, (login, token) =>
                _webDav.ConnectAsync(profile, login, (fingerprint, systemTrusted) => DecideTrust(profile, fingerprint, systemTrusted), token));
            return;
        }
        SetStatus($"{profile.Protocol} is not implemented yet.");
    }

    private async Task ConnectSignedInAsync(FilePane pane, ConnectionProfile profile,
        Func<SftpCredentials, CancellationToken, Task<IFileSystemProvider>> open)
    {
        var login = await CredentialsForAsync(profile);
        while (login is not null)
        {
            var credentials = login;
            var result = await OpenSession(pane, profile, token => open(credentials, token));
            if (result != ConnectResult.Failed) return;
            login = await CredentialsForAsync(profile, forcePrompt: true, previous: credentials);
        }
    }

    private async Task<SftpCredentials?> CredentialsForAsync(ConnectionProfile profile, bool forcePrompt = false, SftpCredentials? previous = null)
    {
        if (!forcePrompt)
        {
            try
            {
                var saved = await _secrets.LoadAsync(profile.Id);
                if (saved is not null && saved.Authentication == profile.Authentication) return saved;
            }
            catch (Exception ex) { SetStatus($"Could not read the saved password: {ex.Message}"); }
        }
        var login = await Dialogs.SftpLoginAsync(this, profile, previous);
        if (login is null) return null;
        try
        {
            if (login.Remember) await _secrets.SaveAsync(profile.Id, login);
            else await _secrets.DeleteAsync(profile.Id);
        }
        catch (Exception ex) { SetStatus($"Could not save the password: {ex.Message}"); }
        return login;
    }

    private static bool IsRemoteRoot(FilePane pane, string path)
    {
        try { return pane.Provider.DisplayPath(pane.Provider.NormalizePath(path)) == "/"; }
        catch (IOException) { return false; }
    }

    private enum ConnectResult { Connected, Failed, Cancelled }

    private async Task<ConnectResult> OpenSession(FilePane pane, ConnectionProfile profile, Func<CancellationToken, Task<IFileSystemProvider>> connect)
    {
        var result = ConnectResult.Connected;
        var ran = false;
        await RunAsync(async () =>
        {
            ran = true;
            SetStatus($"Connecting to {profile.Name}…");
            IFileSystemProvider remote;
            try { remote = await connect(_operationCancellation!.Token); }
            catch (OperationCanceledException) { result = ConnectResult.Cancelled; throw; }
            catch (Exception) { result = ConnectResult.Failed; throw; }
            try
            {
                pane.OpenRemote(remote);
                await pane.LoadLocationsAsync();
                var remembered = pane.StoredSessionPath(profile.Id);
                var start = remembered is not null && remote.CanHandle(remembered) ? remembered
                    : string.IsNullOrWhiteSpace(profile.InitialPath) ? "/" : profile.InitialPath;
                try
                {
                    await pane.NavigateAsync(start);
                    SetStatus(pane.CurrentDisplayPath);
                }
                catch (Exception ex) when (!IsRemoteRoot(pane, start))
                {
                    await pane.NavigateAsync("/");
                    SetStatus($"Could not open {start}. Showing / instead. {ex.Message}");
                }
                TrackSession(profile.Id, profile.Name, remote);
            }
            catch (Exception ex)
            {
                result = ex is OperationCanceledException ? ConnectResult.Cancelled : ConnectResult.Failed;
                pane.CloseRemote();
                if (!PaneUses(remote)) (remote as IDisposable)?.Dispose();
                try
                {
                    await pane.LoadLocationsAsync();
                    await pane.NavigateAsync(pane.SavedPath);
                }
                catch (Exception) { /* The connection error is the one to report. */ }
                throw;
            }
        });
        return ran ? result : ConnectResult.Failed;
    }

    private string PathForPane(FilePane pane, IFileSystemProvider provider, ConnectionProfile? profile)
    {
        if (profile is not null && pane.StoredSessionPath(profile.Id) is { } stored && provider.CanHandle(stored))
            return stored;
        if (ReferenceEquals(pane.SessionProvider, provider) && provider.CanHandle(pane.SessionPath))
            return pane.SessionPath;
        return string.IsNullOrWhiteSpace(profile?.InitialPath) ? "/" : profile.InitialPath;
    }

    private ConnectionProfile? Profile(string id) => _profiles.FirstOrDefault(profile => profile.Id == id);

    private async Task SwitchSessionAsync(FilePane pane, string id)
    {
        if (!_sessions.TryGetValue(id, out var provider)) return;
        var profile = Profile(id);
        if (ReferenceEquals(pane.SessionProvider, provider) && !pane.IsRemote && !ReferenceEquals(pane.Provider, pane.LocalProvider))
            return;
        await AttachSharedAsync(pane, provider, PathForPane(pane, provider, profile));
    }

    private async Task ReconnectSessionAsync(FilePane pane, string id)
    {
        if (_busy || _saving) return;
        var profile = Profile(id);
        if (profile is null) return;
        if (_open.Any(session => session.Id == id && session.Link == SessionLink.Reconnecting)) return;
        if (_sessions.ContainsKey(id)) DisconnectSession(id, quiet: true);
        SetLink(id, SessionLink.Reconnecting);
        await ConnectAsync(pane, profile);
        if (_open.Any(session => session.Id == id && session.Link == SessionLink.Reconnecting))
            SetLink(id, SessionLink.Disconnected);
    }

    private void DisconnectSession(string id, bool quiet = false)
    {
        var chip = _open.FirstOrDefault(session => session.Id == id);
        if (chip is null || chip.Link == SessionLink.Disconnected && !_sessions.ContainsKey(id)) return;
        if (_sessions.TryGetValue(id, out var provider))
        {
            _sessions.Remove(id);
            _live.RemoveAll(item => ReferenceEquals(item, provider));
            _left.DetachSession(provider);
            _right.DetachSession(provider);
            try { (provider as IDisposable)?.Dispose(); }
            catch (Exception) { }
        }
        SetLink(id, SessionLink.Disconnected);
        if (!quiet) SetStatus($"Disconnected: {chip.Name}. The profile is kept.");
    }

    private async Task ForgetSignInAsync(string id)
    {
        var profile = Profile(id);
        if (profile is null) return;
        if (!await _secrets.ExistsAsync(id))
        {
            SetStatus($"No saved sign-in for {profile.Name}.");
            return;
        }
        if (!await Dialogs.ConfirmAsync(this, "Forget sign-in",
                $"Forget the saved sign-in for {profile.Name}? The connection profile stays.", "Forget")) return;
        await _secrets.DeleteAsync(id);
        SetStatus($"Forgot the saved sign-in for {profile.Name}. The next connection will ask again.");
    }

    private void CloseSession(string id)
    {
        var name = _open.FirstOrDefault(session => session.Id == id)?.Name ?? Profile(id)?.Name ?? id;
        if (_sessions.TryGetValue(id, out var provider))
        {
            _sessions.Remove(id);
            _live.RemoveAll(item => ReferenceEquals(item, provider));
            _left.ReleaseSession(provider, id);
            _right.ReleaseSession(provider, id);
            try { (provider as IDisposable)?.Dispose(); }
            catch (Exception) { }
        }
        else
        {
            _left.ForgetSession(id);
            _right.ForgetSession(id);
        }
        _open.RemoveAll(session => session.Id == id);
        PublishConnections();
        SetStatus($"Closed: {name}");
    }

    private void SetLink(string id, SessionLink link)
    {
        var index = _open.FindIndex(session => session.Id == id);
        if (index < 0) return;
        var provider = link == SessionLink.Connected ? _open[index].Provider : null;
        _open[index] = _open[index] with { Link = link, Provider = provider };
        PublishConnections();
    }

    private void NoteConnectionClosed()
    {
        foreach (var pane in new[] { _left, _right })
        {
            var id = _open.FirstOrDefault(session => ReferenceEquals(session.Provider, pane.SessionProvider))?.Id;
            if (id is not null) DisconnectSession(id, quiet: true);
        }
    }

    private static bool IsConnectionClosed(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("connection closed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void PublishConnections()
    {
        for (var i = 0; i < _open.Count; i++)
        {
            var profile = Profile(_open[i].Id);
            if (profile is not null && profile.Name != _open[i].Name)
                _open[i] = _open[i] with { Name = profile.Name };
        }
        _left.SetConnections(_profiles);
        _right.SetConnections(_profiles);
        var links = _open.ToDictionary(session => session.Id, session => session.Link, StringComparer.Ordinal);
        _left.SetSessionLinks(links);
        _right.SetSessionLinks(links);
        var chips = _open.ToArray();
        _left.SetOpenSessions(chips);
        _right.SetOpenSessions(chips);
    }

    private async Task AttachSharedAsync(FilePane pane, IFileSystemProvider provider, string path)
    {
        await RunAsync(async () =>
        {
            pane.OpenRemote(provider);
            await pane.LoadLocationsAsync();
            try
            {
                await pane.NavigateAsync(path);
                SetStatus(pane.CurrentDisplayPath);
            }
            catch (Exception ex) when (!IsRemoteRoot(pane, path))
            {
                await pane.NavigateAsync("/");
                SetStatus($"Could not open {path}. Showing / instead. {ex.Message}");
            }
        });
    }

    private void TrackSession(string id, string name, IFileSystemProvider remote)
    {
        _sessions[id] = remote;
        _open.RemoveAll(session => session.Id == id || ReferenceEquals(session.Provider, remote));
        _open.Insert(0, new OpenSessionChip(id, name, remote));
        if (!_live.Exists(item => ReferenceEquals(item, remote))) _live.Add(remote);
        PublishConnections();
    }

    private bool PaneUses(IFileSystemProvider provider) =>
        ReferenceEquals(_left.SessionProvider, provider) || ReferenceEquals(_right.SessionProvider, provider);

    private void CloseSessions()
    {
        foreach (var provider in _live.ToArray())
        {
            try { (provider as IDisposable)?.Dispose(); }
            catch (Exception) { }
        }
        _live.Clear();
        _sessions.Clear();
        _open.Clear();
        _left.CloseRemote();
        _right.CloseRemote();
    }

    private bool DecideTrust(ConnectionProfile profile, string fingerprint, bool systemTrusted)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return systemTrusted;
        var remembered = false;
        lock (_trustLock)
        {
            if (_trusted.TryGetValue(profile.Id, out var known))
            {
                if (string.Equals(known, fingerprint, StringComparison.Ordinal)) return true;
                remembered = true;
            }
        }
        if (systemTrusted && !remembered)
        {
            RememberTrust(profile, fingerprint);
            return true;
        }
        var title = profile.Protocol == ConnectionProtocols.Sftp ? "Trust server" : "Trust certificate";
        var accepted = AskTrust(title, $"The server presented this identity:\n{fingerprint}\n\nTrust it for this connection? It is remembered until the identity changes.");
        if (accepted) RememberTrust(profile, fingerprint);
        return accepted;
    }

    private void RememberTrust(ConnectionProfile basis, string fingerprint)
    {
        lock (_trustLock) _trusted[basis.Id] = fingerprint;
        void Apply()
        {
            var index = _profiles.FindIndex(item => item.Id == basis.Id);
            if (index < 0 || _profiles[index].Trusts(fingerprint)) return;
            _profiles[index] = _profiles[index] with { HostFingerprint = fingerprint };
            PublishConnections();
            var snapshot = _profiles.ToList();
            _ = SaveConnectionsQuiet(snapshot);
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    private async Task SaveConnectionsQuiet(List<ConnectionProfile> snapshot)
    {
        try { await _connections.SaveAsync(snapshot); }
        catch (Exception ex) { Dispatcher.UIThread.Post(() => SetStatus($"Could not save connections: {ex.Message}")); }
    }

    private bool AskTrust(string title, string message)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try { done.TrySetResult(await Dialogs.ConfirmAsync(this, title, message, "Trust")); }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        return done.Task.GetAwaiter().GetResult();
    }

    private async Task<string> RequireLocalFileAsync(FilePane pane, FileEntry entry) =>
        await pane.Provider.GetLocalFilePathAsync(entry.FullPath, _operationCancellation!.Token)
        ?? throw new NotSupportedException("This provider cannot expose a local file for an external application.");

    private async Task ViewAsync()
    {
        if (RemoteBlocksFiles()) return;
        if (_active.CurrentEntry is not { IsDirectory: false } entry)
        { SetStatus("Select a file to view."); return; }
        SetStatus($"Reading: {entry.Name} · Esc to cancel");
        var preview = await _textPreview.ReadAsync(_active.Provider, entry.FullPath, _operationCancellation!.Token);
        await new TextViewerWindow(this, entry, preview).ShowDialog(this);
        SetStatus($"Viewed: {entry.Name}");
    }

    private async Task EditAsync()
    {
        if (RemoteBlocksFiles()) return;
        if (_active.CurrentEntry is not { IsDirectory: false } entry)
        { SetStatus("Select a file to edit."); return; }
        TextDocument document;
        try
        {
            document = await TextDocuments.ReadAsync(_active.Provider, entry.FullPath, TextDocuments.MaxEditBytes, allowTruncate: false, _operationCancellation!.Token);
        }
        catch (InvalidDataException)
        {
            await OpenWithSystemAsync(entry);
            return;
        }
        if (!_active.Provider.CanWriteFileContent)
            throw new NotSupportedException("This provider cannot edit files.");
        var editor = new TextEditorWindow(this, entry, _active.Provider, document);
        await editor.ShowDialog(this);
        if (editor.DidSave) await _active.NavigateAsync(_active.CurrentPath, entry.FullPath);
        SetStatus(editor.DidSave ? $"Saved: {entry.Name}" : $"Closed: {entry.Name}");
    }

    private async Task OpenWithSystemAsync(FileEntry entry)
    {
        var localPath = await _active.Provider.GetLocalFilePathAsync(entry.FullPath, _operationCancellation!.Token)
            ?? throw new NotSupportedException("This file is not text, and this connection cannot hand it to a local application.");
        await _launcher.OpenAsync(localPath, _operationCancellation!.Token);
        SetStatus($"Opened: {entry.Name}");
    }

    private async Task EditExternalAsync()
    {
        if (RemoteBlocksFiles()) return;
        if (_active.CurrentEntry is not { IsDirectory: false } entry)
        { SetStatus("Select a file to edit."); return; }
        var localPath = await RequireLocalFileAsync(_active, entry);
        await _launcher.EditAsync(localPath, _editorExecutable, _operationCancellation!.Token);
        SetStatus($"Opened in external editor: {entry.Name} · Ctrl+R refreshes after saving");
    }

    private async Task ChooseEditorAsync()
    {
        var editor = await Dialogs.PromptAsync(this, "External editor",
            "Enter the editor executable path or command name, without arguments or surrounding quotes.\n\n" +
            "The file menu opens the current file in this editor. Leave empty to use Notepad on Windows. On Linux, enter an editor command.",
            _editorExecutable, allowEmpty: true);
        if (editor is null) return;
        _editorExecutable = editor.Trim();
        SetStatus(_editorExecutable.Length == 0 ? "Using the platform default editor." : $"Editor: {_editorExecutable}");
    }

    private async Task RenameAsync()
    {
        if (RemoteBlocksFiles()) return;
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
        IReadOnlyList<FileEntry>? draggedEntries = null, string? targetDirectory = null, string? sourceDescription = null,
        IFileSystemProvider? sourceProvider = null)
    {
        var source = sourcePane ?? _active; var destination = targetPane ?? (source == _left ? _right : _left);
        if (source.IsRemote || destination.IsRemote)
        { SetStatus("Switch both panes to Local before copying or moving files."); return; }
        var entries = draggedEntries ?? source.OperationEntries;
        var destinationDirectory = targetDirectory ?? destination.CurrentPath;
        if (entries.Count == 0) { SetStatus("Select a file or folder first."); return; }
        if (!await Dialogs.ConfirmAsync(this, move ? "Move" : "Copy",
                $"{(move ? "Move" : "Copy")} {Describe(entries)}\n\nFrom: {sourceDescription ?? source.CurrentPath}\nTo: {destinationDirectory}",
                move ? "Move" : "Copy")) return;
        var snapshot = entries.ToArray();
        var leaf = System.IO.Path.GetFileName(destinationDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(leaf)) leaf = destinationDirectory;
        var what = snapshot.Length == 1 ? snapshot[0].Name : $"{snapshot.Length} entries";
        var label = $"{(move ? "Move" : "Copy")} {what} → {leaf}";
        _transfers.Enqueue(new TransferRequest
        {
            Source = sourceProvider ?? source.Provider, Destination = destination.Provider, Entries = snapshot,
            DestinationDirectory = destinationDirectory, Move = move, Label = label,
            ResolveConflict = conflict => ResolveConflictOnUi(conflict, CancellationToken.None),
            ResolveConflictWithCancellation = ResolveConflictOnUi
        });
        SetStatus($"{label} queued.");
    }

    private Task<ConflictResolution> ResolveConflictOnUi(FileConflict conflict, CancellationToken cancellationToken)
    {
        if (Dispatcher.UIThread.CheckAccess()) return Dialogs.ConflictAsync(this, conflict, cancellationToken);
        var done = new TaskCompletionSource<ConflictResolution>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try { done.TrySetResult(await Dialogs.ConflictAsync(this, conflict, cancellationToken)); }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        return done.Task;
    }

    private async Task FinishTransferAsync(string message)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                foreach (var pane in new[] { _left, _right })
                {
                    try { await pane.RefreshAsync(); }
                    catch (Exception ex) { message += $" Refresh failed: {ex.Message}"; }
                }
                SetStatus(message);
                if (_transfers.WaitingCount == 0 && !_busy && !_saving && !OwnedWindows.Any()) _active.FocusList();
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        await done.Task;
    }

    private void CancelRunningTransfer()
    {
        _transfers.CancelRunning();
        _cancelTransfer.IsEnabled = false;
        _transferText.Text = "Cancelling… Completed files will be kept.";
    }

    private void UpdateQueuePanel()
    {
        var jobs = _transfers.Snapshot();
        _queueHost.Children.Clear();
        var waiting = jobs.Where(job => job.State == TransferQueueState.Waiting).ToArray();
        if (waiting.Length > 0)
        {
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            header.Children.Add(new TextBlock { Text = $"Waiting · {waiting.Length}", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var clear = new Button { Name = "ClearWaitingTransfers", Content = "Clear waiting", Padding = new Thickness(8, 2) };
            clear.Classes.Add("flat");
            clear.Click += (_, _) => _transfers.ClearWaiting();
            Grid.SetColumn(clear, 1); header.Children.Add(clear);
            _queueHost.Children.Add(header);
            foreach (var job in waiting)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                var caption = new TextBlock { Text = job.Label, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(caption, job.DestinationDirectory);
                row.Children.Add(caption);
                var remove = new Button { Name = "RemoveQueuedTransfer", Content = "Remove", Padding = new Thickness(8, 2), Tag = job.Id };
                remove.Classes.Add("flat");
                remove.Click += (_, _) => _transfers.Cancel(job.Id);
                Grid.SetColumn(remove, 1); row.Children.Add(remove);
                _queueHost.Children.Add(row);
            }
        }
        var running = jobs.FirstOrDefault(job => job.State == TransferQueueState.Running);
        _jobText.Text = running?.Label ?? (waiting.Length > 0 ? "Waiting to start…" : "");
        _transferPanel.IsVisible = jobs.Count > 0;
        _cancelTransfer.IsEnabled = running is not null && !_transfers.IsCancelRequested;
        if (jobs.Count == 0)
        {
            _transferTimer?.Stop();
            return;
        }
        _transferTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        if (_transferTimer.Tag is null)
        {
            _transferTimer.Tag = true;
            _transferTimer.Tick += (_, _) => PaintTransfer();
        }
        if (!_transferTimer.IsEnabled) _transferTimer.Start();
        PaintTransfer();
    }

    private void PaintTransfer()
    {
        if (_transfers.IsCancelRequested)
        {
            _transferText.Text = "Cancelling… Completed files will be kept.";
            _cancelTransfer.IsEnabled = false;
        }
        var total = _transfers.BatchProgress;
        if (total is null)
        {
            _batchBar.IsIndeterminate = true;
            _batchText.Text = "Scanning files…";
        }
        else
        {
            _batchBar.IsIndeterminate = total.IsScanning;
            var percent = total.TotalBytes is > 0 ? 100d * total.ProcessedBytes / total.TotalBytes.Value :
                total.TotalFiles > 0 ? 100d * total.ProcessedFiles / total.TotalFiles : 0;
            _batchBar.Value = total.IsCompleted ? 100 : Math.Clamp(percent, 0, 99.9);
            _batchText.Text = total.IsScanning ? $"Scanning: {total.Path}" :
                $"Overall: {total.ProcessedFiles} / {total.TotalFiles} files · skipped: {total.SkippedFiles}" +
                (total.TotalBytes is long bytes ? $" · {total.ProcessedBytes / 1048576d:0.0} / {bytes / 1048576d:0.0} MiB processed" : "");
        }
        var value = _transfers.FileProgress;
        if (value is null || _transfers.IsCancelRequested) return;
        _transferBar.IsIndeterminate = value.FileSize is null or <= 0;
        _transferBar.Value = value.FileSize is > 0 ? Math.Clamp(100d * value.BytesTransferred / value.FileSize.Value, 0, 100) : 0;
        _transferText.Text = $"{value.Path} · {value.BytesTransferred / 1048576d:0.0} MiB" +
            (value.FileSize is long size ? $" / {size / 1048576d:0.0} MiB (current file)" : "");
        ToolTip.SetTip(_transferText, value.Path);
    }

    internal bool TransfersIdle => _transfers.IsIdle;
    internal int WaitingTransfers => _transfers.WaitingCount;

    private async Task CreateDirectoryAsync()
    {
        if (RemoteBlocksFiles()) return;
        var pane = _active;
        var name = await Dialogs.PromptAsync(this, "Create directory", $"In: {pane.CurrentPath}\n\nDirectory name:");
        if (name is null) return;
        await _operations.CreateDirectoryAsync(pane.Provider, pane.CurrentPath, name, _operationCancellation!.Token);
        await pane.NavigateAsync(pane.CurrentPath, pane.Provider.CombinePath(pane.CurrentPath, name));
        SetStatus($"Created: {name}");
    }

    private async Task CreateFileAsync()
    {
        if (RemoteBlocksFiles()) return;
        var pane = _active;
        if (!pane.Provider.CanWriteFileContent)
            throw new NotSupportedException("This provider cannot create files.");
        var name = await Dialogs.PromptAsync(this, "New file", $"In: {pane.CurrentDisplayPath}\n\nFile name:");
        if (name is null) return;
        var path = await _operations.CreateFileAsync(pane.Provider, pane.CurrentPath, name, _operationCancellation!.Token);
        await pane.NavigateAsync(pane.CurrentPath, path);
        var entry = await pane.Provider.GetEntryAsync(path, _operationCancellation!.Token)
            ?? throw new FileNotFoundException("The new file is no longer there.", path);
        var document = await TextDocuments.ReadAsync(pane.Provider, path, TextDocuments.MaxEditBytes, allowTruncate: false, _operationCancellation!.Token);
        var editor = new TextEditorWindow(this, entry, pane.Provider, document);
        await editor.ShowDialog(this);
        if (editor.DidSave) await pane.NavigateAsync(pane.CurrentPath, path);
        SetStatus(editor.DidSave ? $"Saved: {entry.Name}" : $"Created: {entry.Name}");
    }

    private async Task DeleteAsync(bool permanent)
    {
        if (RemoteBlocksFiles()) return;
        var pane = _active; var entries = pane.OperationEntries;
        if (entries.Count == 0) { SetStatus("Select a file or folder first."); return; }
        var recycle = !permanent && pane.Provider.SupportsRecycle;
        var warning = recycle
            ? "You can restore these items from the system recycle bin."
            : pane.Provider.SupportsRecycle
                ? "This bypasses the recycle bin and cannot be undone here."
                : "This pane has no recycle bin. The items will be deleted permanently.";
        if (!await Dialogs.ConfirmAsync(this, recycle ? "Move to recycle bin" : "Delete permanently",
                $"{(recycle ? "Recycle" : "Permanently delete")} {Describe(entries, listNames: true)}\n\n{warning}",
                recycle ? "Recycle" : "Delete permanently")) return;
        await WithRefreshAsync(async () =>
        {
            await _operations.DeleteAsync(pane.Provider, entries, recycle ? DeleteMode.Recycle : DeleteMode.Permanent,
                Progress(), _operationCancellation!.Token);
            return recycle ? "Moved to recycle bin." : "Permanently deleted.";
        });
    }

    private async Task WithRefreshAsync(Func<Task<string>> operation)
    {
        string message;
        try { message = await operation(); }
        catch (PartialDeletionException ex) when (ex.InnerException is OperationCanceledException)
        {
            message = ex.Message;
        }
        catch (OperationCanceledException)
        {
            message = "Cancelled. Already completed changes are kept.";
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
        SetStatus(pane.CurrentDisplayPath);
    }

    private async Task FindFilesAsync()
    {
        if (RemoteBlocksFiles()) return;
        var pane = _active;
        var hit = await FindFilesWindow.ShowAsync(this, pane.Provider, pane.CurrentDisplayPath);
        if (hit is null) return;
        SetActive(pane);
        if (hit.IsDirectory) await pane.NavigateAsync(hit.FullPath);
        else await pane.NavigateAsync(hit.ParentDirectory, hit.FullPath);
        pane.FocusList();
    }

    private async Task CalculateFolderSizeAsync(FilePane pane, FileEntry entry)
    {
        if (!_folderSizes.TryBegin(entry.FullPath, pane.Provider.PathComparer)) return;
        if (_sizingPane is not null && _sizingPath is not null) _sizingPane.ShowCalculatedSize(_sizingPath, null);
        _sizeCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _sizeCancellation = cancellation;
        var generation = ++_sizeGeneration;
        var path = entry.FullPath;
        _sizingPane = pane;
        _sizingPath = path;
        pane.ShowCalculatedSize(path, "Calculating…");
        try
        {
            var size = await FolderSizeCalculator.MeasureAsync(pane.Provider, path, cancellation.Token);
            if (generation != _sizeGeneration) return;
            pane.ShowCalculatedSize(path, FilePane.FormatSize(size.Bytes) + (size.Partial ? "+" : ""));
        }
        catch (OperationCanceledException)
        {
            if (generation == _sizeGeneration) pane.ShowCalculatedSize(path, null);
        }
        catch (Exception ex)
        {
            if (generation != _sizeGeneration) return;
            pane.ShowCalculatedSize(path, null);
            SetStatus($"Error: {ex.Message}");
        }
        finally
        {
            if (generation == _sizeGeneration)
            {
                _folderSizes.End();
                _sizeCancellation = null;
                _sizingPane = null;
                _sizingPath = null;
            }
            cancellation.Dispose();
        }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_left.IsDragging || _right.IsDragging) return;
        if (_busy)
        {
            if (e.Key == Key.Escape) _operationCancellation?.Cancel();
            e.Handled = true; return;
        }
        var typing = e.Source is Visual source && (source is TextBox || source.GetVisualAncestors().OfType<TextBox>().Any());
        if (e.Key == Key.Escape && !_transfers.IsIdle && !typing)
        {
            e.Handled = true; CancelRunningTransfer(); return;
        }
        if (e.Key == Key.Escape && _sizeCancellation is not null && !typing)
        {
            e.Handled = true; _sizeCancellation.Cancel(); return;
        }
        var find = e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control
            || e.Key == Key.F7 && e.KeyModifiers == KeyModifiers.Alt;
        if (find)
        {
            e.Handled = true;
            if (_active.IsRemote)
            {
                SetStatus("This pane is showing connections. Switch it to Local for files.");
                return;
            }
            _ = FindFilesAsync();
            return;
        }
        var onButton = e.Source is Visual keySource && (keySource is Button || keySource.GetVisualAncestors().OfType<Button>().Any());
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && !typing && !onButton
            && _active.CurrentEntry is { IsDirectory: true, IsLink: false } sized && sized.Name != "..")
        {
            e.Handled = true;
            _ = CalculateFolderSizeAsync(_active, sized);
            return;
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
        if (_active.IsRemote && IsFileCommand(e))
        {
            e.Handled = true;
            SetStatus("This pane is showing connections. Switch it to Local for files.");
            return;
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
            Key.F4 when e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift) => ChooseEditorAsync,
            Key.F4 when e.KeyModifiers.HasFlag(KeyModifiers.Shift) => CreateFileAsync,
            Key.F4 => EditAsync,
            Key.F5 => () => TransferAsync(false),
            Key.F6 when e.KeyModifiers.HasFlag(KeyModifiers.Shift) => RenameAsync,
            Key.F6 => () => TransferAsync(true),
            Key.F7 => CreateDirectoryAsync,
            Key.F8 => () => DeleteAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift)),
            Key.Enter => () => OpenAsync(_active),
            Key.R when e.KeyModifiers.HasFlag(KeyModifiers.Control) => async () =>
            {
                await _active.LoadLocationsAsync(); await _active.NavigateAsync(_active.CurrentPath); SetStatus(_active.CurrentDisplayPath);
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
        if (_busy || !_transfers.IsIdle)
        {
            SetStatus(_busy
                ? "An operation is in progress. Press Esc to cancel, then close the window."
                : "A copy or move is still queued. Cancel it, then close the window.");
            return;
        }
        if (_saving) return;
        _saving = true;
        try
        {
            await _settings.SaveAsync(new(_left.SavedPath, _right.SavedPath, _normalWidth, _normalHeight,
                _active == _left, WindowState == WindowState.Maximized,
                RequestedThemeVariant == ThemeVariant.Dark ? "Dark" : "Light", _editorExecutable));
            CloseSessions();
            _allowClose = true; Close();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save settings: {ex.Message}");
            if (await Dialogs.ConfirmAsync(this, "Settings could not be saved", ex.Message + "\n\nClose without saving settings?", "Close"))
            {
                CloseSessions();
                _allowClose = true; Close();
            }
        }
        finally { _saving = false; }
    }
}
