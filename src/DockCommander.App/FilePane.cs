using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using DockCommander.Core.Connections;
using DockCommander.Core.Files;

namespace DockCommander.App;

internal enum SessionLink { Connected, Reconnecting, Disconnected }

internal sealed record OpenSessionChip(string Id, string Name, IFileSystemProvider? Provider, SessionLink Link = SessionLink.Connected);

internal sealed class FileRow(FileEntry entry) : INotifyPropertyChanged
{
    public FileEntry Entry { get; } = entry;
    private bool _marked;
    public bool Marked
    {
        get => _marked;
        set { _marked = value; PropertyChanged?.Invoke(this, new(nameof(Marker))); }
    }
    public string Marker => Marked ? "●" : "";
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class FilePane : UserControl
{
    internal sealed record DraggedEntries(FilePane Source, IReadOnlyList<FileEntry> Entries);
    internal static readonly DataFormat<DraggedEntries> DragFormat = DataFormat.CreateInProcessFormat<DraggedEntries>("DockCommander.Files");
    private PointerPressedEventArgs? _dragPress;
    private Point _dragStart;
    private FileRow? _dragRow;
    private Control? _dropRow;
    public bool IsDragging { get; private set; }
    public event Action<FilePane, string>? ActionRequested;
    public event Action<FilePane, IReadOnlyList<string>, PixelPoint>? SystemMenuRequested;
    private bool _systemMenuGesture;
    public event Action<FilePane, DraggedEntries, string, bool>? DropRequested;
    public event Action<FilePane, IReadOnlyList<string>, string>? ExternalDropRequested;
    public event Action<string>? InteractionError;
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Single, Focusable = true };
    private readonly TextBox _path = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _locations = new() { Orientation = Orientation.Horizontal, Spacing = 3 };
    private readonly Grid _locationHost = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
    private readonly Button _moreLocations = new() { Name = "MoreLocations", Content = "Ещё ▾", IsVisible = false,
        Width = 76, Padding = new Thickness(7, 4), MinHeight = 28 };
    private readonly Border _frame;
    private readonly Grid _local;
    private readonly RemotePaneView _remote = new();
    private readonly Button _localMode = new() { Name = "LocalMode", Content = "Local" };
    private readonly Button _remoteMode = new() { Name = "RemoteMode", Content = "Remote" };
    private readonly Button _savedConnections = new()
    {
        Name = "SavedConnections", Content = UiIcons.Create("add", size: 14), IsVisible = false,
        Width = 28, Height = 28, MinHeight = 0, Padding = new Thickness(0),
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0)
    };
    private readonly TextBlock _count = new() { FontSize = 12 };
    private readonly List<(string Path, Button Button)> _locationButtons = [];
    private List<FileRow> _rows = [];
    private int _anchor;
    private HashSet<string>? _rangeMarkBase;
    private Task _navigation = Task.CompletedTask;
    private readonly IFileSystemProvider _homeProvider;
    private IFileSystemProvider? _remoteProvider;
    private string? _remotePath;
    private string _localPath;
    public IFileSystemProvider Provider { get; private set; }
    public IFileSystemProvider LocalProvider => _homeProvider;
    public string SavedPath => _localPath;
    public string CurrentDisplayPath => Provider.DisplayPath(CurrentPath);

    public void UseProvider(IFileSystemProvider provider) => Provider = provider;
    public string CurrentPath { get; private set; }
    public event Action<FilePane>? Activated;
    public event Action<FilePane, string>? NavigateRequested;
    public event Action<FilePane>? OpenRequested;
    public event Action? AddConnectionRequested;
    public event Action<ConnectionProfile>? EditConnectionRequested;
    public event Action<ConnectionProfile>? ConnectRequested;
    public event Action<FilePane>? ReturnedToLocal;
    public event Action<FilePane>? RemoteRestored;
    public event Action<FilePane>? RemoteWithoutSession;
    public event Action<FilePane, string>? SessionChosen;
    public event Action<FilePane, string>? SessionReconnectRequested;
    public event Action<FilePane, string>? SessionEditRequested;
    public event Action<FilePane, string>? SessionDisconnectRequested;
    public event Action<FilePane, string>? SessionForgetRequested;
    public event Action<FilePane, string>? SessionCloseRequested;
    public IFileSystemProvider? SessionProvider => _remoteProvider;
    private readonly Dictionary<string, string> _sessionPaths = new(StringComparer.Ordinal);
    private List<OpenSessionChip> _openSessions = [];
    public string SessionPath => _remotePath ?? CurrentPath;
    public bool IsRemote { get; private set; }

    public FilePane(IFileSystemProvider provider, string path, string label = "FILES")
    {
        _homeProvider = provider;
        _localPath = path;
        Provider = provider; CurrentPath = path;
        _ = label;
        _list.Classes.Add("file-list");
        _path.Classes.Add("path-field");
        _count.Classes.Add("muted");
        ToolTip.SetTip(_path, "Current path · Ctrl+L to edit");
        _list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _path.Text = path;
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var locationsRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        _locationHost.Children.Add(new Border { Child = _locations, ClipToBounds = true });
        _moreLocations.Classes.Add("flat");
        Grid.SetColumn(_moreLocations, 1); _locationHost.Children.Add(_moreLocations);
        _moreLocations.Click += (_, _) => _moreLocations.ContextMenu?.Open(_moreLocations);
        _locationHost.SizeChanged += (_, _) => ArrangeLocations();
        locationsRow.Children.Add(_locationHost);
        var header = new Border { Child = Row("", "Name", "Type", "Size", "Modified", true) };
        header.Classes.Add("column-header"); Add(header, 0);
        _list.ItemTemplate = new FuncDataTemplate<FileRow>((row, _) =>
        {
            if (row is null) return new TextBlock();
            var entry = row.Entry;
            var cells = Row("", entry.Name, entry.IsLink ? "Link" : entry.Name == ".." ? "Parent" : entry.IsDirectory ? "Folder" : entry.Extension?.TrimStart('.') ?? "",
                entry.Size is long size ? FormatSize(size) : "", entry.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "", false);
            ((TextBlock)cells.Children[0]).Bind(TextBlock.TextProperty, new Binding(nameof(FileRow.Marker)));
            var icon = UiIcons.Create(entry.Name == ".." ? "up" : entry.IsDirectory ? "folder" : "file", entry.IsDirectory && entry.Name != ".." ? "folder" : null);
            Grid.SetColumn(icon, 1); cells.Children.Add(icon);
            cells.DataContext = row;
            ToolTip.SetTip(cells, entry.FullPath);
            return cells;
        });
        Add(_list, 1);
        var footer = new Border { Child = _count }; footer.Classes.Add("pane-footer"); Add(footer, 2);
        _local = grid;
        foreach (var mode in new[] { _localMode, _remoteMode })
        {
            mode.Classes.Add("flat");
            mode.FontSize = 10;
            mode.FontWeight = FontWeight.SemiBold;
            mode.Padding = new Thickness(4, 0);
            mode.MinHeight = 0;
            mode.Height = 18;
            mode.VerticalAlignment = VerticalAlignment.Center;
        }
        _savedConnections.Classes.Add("flat");
        _savedConnections.Click += (_, _) => { Activated?.Invoke(this); ShowCatalog(); };
        ToolTip.SetTip(_savedConnections, "Saved connections");
        Grid.SetColumn(_savedConnections, 1);
        locationsRow.Children.Add(_savedConnections);
        _localMode.Click += (_, _) => ShowLocal();
        _remoteMode.Click += (_, _) =>
        {
            if (_remoteProvider is not null) ShowRemoteSession();
            else if (RemoteWithoutSession is { } requested) requested(this);
            else ShowCatalog();
        };
        _remote.AddRequested += () => AddConnectionRequested?.Invoke();
        _remote.EditRequested += profile => EditConnectionRequested?.Invoke(profile);
        _remote.ConnectRequested += profile => ConnectRequested?.Invoke(profile);
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        modes.Children.Add(_localMode); modes.Children.Add(_remoteMode);
        Grid.SetColumn(modes, 2); locationsRow.Children.Add(modes);
        var top = new StackPanel(); top.Children.Add(locationsRow); top.Children.Add(_path);
        var topBorder = new Border { Child = top }; topBorder.Classes.Add("pane-top");
        var pages = new Grid();
        pages.Children.Add(_local); pages.Children.Add(_remote);
        var shell = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        shell.Children.Add(topBorder);
        Grid.SetRow(pages, 1); shell.Children.Add(pages);
        _frame = new Border { Child = shell }; _frame.Classes.Add("pane-frame");
        Content = _frame;
        ShowFiles(false);
        GotFocus += (_, _) => Activated?.Invoke(this);
        _path.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            NavigateRequested?.Invoke(this, _path.Text ?? CurrentPath);
        };
        _list.DoubleTapped += (_, e) =>
        {
            if (e.Source is Visual source &&
                (source is ListBoxItem || source.GetVisualAncestors().OfType<ListBoxItem>().Any()))
                OpenRequested?.Invoke(this);
        };
        _list.SelectionChanged += (_, _) => UpdateCount();
        _list.AddHandler(KeyDownEvent, ListKeyDown, RoutingStrategies.Tunnel);
        _list.AddHandler(PointerPressedEvent, ListPointerPressed, RoutingStrategies.Tunnel);
        _list.PointerMoved += DragPointerMoved;
        _list.AddHandler(PointerReleasedEvent, (_, _) => { _dragPress = null; _dragRow = null; }, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(_list, true);
        _list.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _list.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropHighlight());
        _list.AddHandler(DragDrop.DropEvent, OnDrop);
        var menu = new ContextMenu();
        foreach (var (caption, action) in new[] { ("Open", "open"), ("View · F3", "view"), ("Edit · F4", "edit"),
                     ("External editor", "external"), ("Choose external editor", "chooseeditor"),
                     ("Copy to other panel · F5", "copy"), ("Move to other panel · F6", "move"),
                     ("Rename · Shift+F6", "rename"), ("New file · Shift+F4", "newfile"), ("New folder · F7", "mkdir"),
                     ("Recycle · F8", "recycle"), ("Refresh · Ctrl+R", "refresh") })
        {
            var item = new MenuItem { Header = caption, Name = "Context_" + action };
            item.Click += (_, _) => ActionRequested?.Invoke(this, action);
            menu.Items.Add(item);
        }
        _list.ContextMenu = menu;
        menu.Opening += (_, e) =>
        {
            if (_systemMenuGesture && SystemMenuRequested is not null) e.Cancel = true;
        };
        _list.ContextRequested += (_, e) =>
        {
            var systemMenu = _systemMenuGesture;
            _systemMenuGesture = false;
            var onRow = false;
            if (e.TryGetPosition(_list, out var point))
            {
                var hit = _list.InputHitTest(point) as Visual;
                var row = hit as ListBoxItem ?? hit?.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
                onRow = row?.DataContext is FileRow;
            }
            else if (e.Source is Visual source)
            {
                var row = source as ListBoxItem ?? source.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
                onRow = row?.DataContext is FileRow;
            }
            ConfigureContextMenu(onRow);
            if (systemMenu && SystemMenuRequested is not null && e.TryGetPosition(_list, out var menuPoint))
            {
                e.Handled = true;
                // The parent row is navigation, not an operation target.
                if (onRow && CurrentEntry?.Name == "..") return;
                var paths = onRow ? OperationEntries.Select(entry => entry.FullPath).ToArray() : [];
                SystemMenuRequested.Invoke(this, paths, _list.PointToScreen(menuPoint));
            }
        };
        void Add(Control control, int row) { Grid.SetRow(control, row); grid.Children.Add(control); }
    }

    private void ConfigureContextMenu(bool onRow)
    {
        if (_list.ContextMenu is null) return;
        foreach (var item in _list.ContextMenu.Items.OfType<MenuItem>())
        {
            var action = item.Name is { } name && name.StartsWith("Context_", StringComparison.Ordinal)
                ? name["Context_".Length..] : "";
            if (action == "recycle") item.Header = Provider.SupportsRecycle ? "Recycle · F8" : "Delete · F8";
            item.IsEnabled = onRow || action is "mkdir" or "newfile" or "refresh";
        }
    }

    private static Grid Row(string mark, string name, string type, string size, string modified, bool header)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("16,26,*,55,78,132"), Margin = new Thickness(6, 0, 10, 0), MinHeight = header ? 18 : 29 };
        var texts = new[] { mark, name, type, size, modified };
        for (var i = 0; i < texts.Length; i++)
        {
            var text = new TextBlock
            {
                Text = texts[i], FontSize = header || i >= 2 ? 12 : 13, FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = i == 3 ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
                Margin = i == 3 ? new Thickness(2, 2, 14, 2) : i == 1 ? new Thickness(2, 2, 12, 2) : new Thickness(2, 2)
            };
            if (header || i >= 2) text.Classes.Add("muted");
            if (i == 0) text.Classes.Add("mark");
            Grid.SetColumn(text, i == 0 ? 0 : i + 1); grid.Children.Add(text);
        }
        return grid;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    public void OpenRemote(IFileSystemProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        RememberSessionPath();
        if (ReferenceEquals(Provider, _homeProvider)) _localPath = CurrentPath;
        _remoteProvider = provider;
        _remotePath = null;
        Provider = provider;
        ShowFiles(true);
    }

    public string? StoredSessionPath(string id) => _sessionPaths.TryGetValue(id, out var path) ? path : null;

    public void SetOpenSessions(IReadOnlyList<OpenSessionChip> sessions)
    {
        _openSessions = sessions.ToList();
        if (UsesSessionBar()) RenderSessions();
        else if (_locationButtons.Any(item => IsSessionPath(item.Path))) ClearLocationButtons();
    }

    public void DetachSession(IFileSystemProvider provider)
    {
        if (!ReferenceEquals(_remoteProvider, provider)) return;
        RememberSessionPath();
        var viewingRemote = IsRemote || !ReferenceEquals(Provider, _homeProvider);
        _remoteProvider = null;
        _remotePath = null;
        if (!viewingRemote) return;
        Provider = _homeProvider;
        ShowCatalog();
    }

    public void ForgetSession(string id) => _sessionPaths.Remove(id);

    public void ReleaseSession(IFileSystemProvider provider, string id)
    {
        _sessionPaths.Remove(id);
        _openSessions.RemoveAll(session => session.Id == id);
        if (!ReferenceEquals(_remoteProvider, provider))
        {
            if (UsesSessionBar()) RenderSessions();
            return;
        }
        var viewingRemote = IsRemote || !ReferenceEquals(Provider, _homeProvider);
        _remoteProvider = null;
        _remotePath = null;
        if (!viewingRemote) return;
        Provider = _homeProvider;
        ShowCatalog();
    }

    public void CloseRemote()
    {
        _remoteProvider = null;
        _remotePath = null;
        if (!ReferenceEquals(Provider, _homeProvider)) Provider = _homeProvider;
        ShowFiles(false);
    }

    public void ShowConnectionList() => ShowCatalog();

    private void ShowLocal()
    {
        if (_remoteProvider is not null && !ReferenceEquals(Provider, _homeProvider))
        {
            RememberSessionPath();
            _remotePath = CurrentPath;
            Provider = _homeProvider;
            ShowFiles(false);
            ReturnedToLocal?.Invoke(this);
            return;
        }
        ShowFiles(false);
    }

    private void ShowRemoteSession()
    {
        if (_remoteProvider is null)
        {
            ShowCatalog();
            return;
        }
        var restore = !ReferenceEquals(Provider, _remoteProvider);
        Provider = _remoteProvider;
        ShowFiles(true);
        if (restore) RemoteRestored?.Invoke(this);
    }

    private void ShowCatalog()
    {
        IsRemote = true;
        _local.IsVisible = false;
        _remote.IsVisible = true;
        _localMode.Classes.Set("chosen", false);
        _remoteMode.Classes.Set("chosen", true);
        _remote.RefreshConnections();
        UpdateSessionButton();
        if (_openSessions.Count > 0) RenderSessions();
        else if (_locationButtons.Any(item => IsSessionPath(item.Path))) ClearLocationButtons();
    }

    private void ShowFiles(bool remoteSession)
    {
        IsRemote = false;
        _local.IsVisible = true;
        _remote.IsVisible = false;
        _localMode.Classes.Set("chosen", !remoteSession);
        _remoteMode.Classes.Set("chosen", remoteSession);
        UpdateSessionButton();
    }

    private void UpdateSessionButton() =>
        _savedConnections.IsVisible = _remoteProvider is not null && !ReferenceEquals(Provider, _homeProvider);

    public void SetConnections(IReadOnlyList<ConnectionProfile> connections) => _remote.SetConnections(connections);

    public void SetSessionLinks(IReadOnlyDictionary<string, SessionLink> links) => _remote.SetSessionLinks(links);

    public void SetActive(bool active)
    {
        _frame.Classes.Set("active", active);
    }

    public async Task LoadLocationsAsync()
    {
        if (UsesSessionBar()) RenderSessions();
        else SetLocations(await Provider.GetLocationsAsync());
    }

    internal void SetLocations(IReadOnlyList<LocationEntry> locations)
    {
        _locations.Children.Clear();
        _locationButtons.Clear();
        foreach (var location in locations)
        {
            var display = location.DisplayName.TrimEnd('\\', '/');
            var content = UiIcons.Label(location.Kind == "Home" ? "folder" : "drive", display.Length == 0 ? location.DisplayName : display);
            var caption = content.Children.OfType<TextBlock>().Single();
            caption.MaxWidth = 74; caption.TextTrimming = TextTrimming.CharacterEllipsis;
            var button = new Button { Content = content, Padding = new Thickness(9, 4), MinHeight = 28 };
            Avalonia.Automation.AutomationProperties.SetName(button, location.DisplayName);
            button.Classes.Add("flat");
            ToolTip.SetTip(button, location.RootPath);
            button.Click += (_, _) => { Activated?.Invoke(this); NavigateRequested?.Invoke(this, location.RootPath); };
            _locations.Children.Add(button);
            _locationButtons.Add((location.RootPath, button));
        }
        UpdateLocationSelection();
        ArrangeLocations();
    }

    private void ArrangeLocations()
    {
        var width = _locationHost.Bounds.Width;
        if (width <= 0) return;
        var widths = _locationButtons.Select(item =>
        {
            // Hidden controls do not measure, so measure their content directly.
            var content = (Control)item.Button.Content!;
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return content.DesiredSize.Width + 20 + _locations.Spacing;
        }).ToArray();
        var overflow = widths.Sum() > width;
        var available = width - (overflow ? _moreLocations.Width + 3 : 0);
        var menu = new ContextMenu();
        var used = 0d;
        var full = false;
        for (var i = 0; i < _locationButtons.Count; i++)
        {
            var (path, button) = _locationButtons[i];
            full |= used + widths[i] > available;
            button.IsVisible = !full;
            if (!full) used += widths[i];
            else
            {
                var caption = Avalonia.Automation.AutomationProperties.GetName(button);
                var item = new MenuItem { Header = SessionId(path) is null ? $"{caption} — {path}" : caption };
                var target = path;
                item.Click += (_, _) =>
                {
                    Activated?.Invoke(this);
                    if (SessionId(target) is { } id) SessionChosen?.Invoke(this, id);
                    else NavigateRequested?.Invoke(this, target);
                };
                menu.Items.Add(item);
            }
        }
        _moreLocations.IsVisible = menu.Items.Count > 0;
        _moreLocations.ContextMenu = menu;
        _moreLocations.Classes.Set("chosen", _locationButtons.Any(item => !item.Button.IsVisible && item.Button.Classes.Contains("chosen")));
    }

    public Task NavigateAsync(string path, string? selectPath = null, string? renamedFrom = null) =>
        QueueNavigation(() => NavigateCore(path, selectPath, renamedFrom));

    public Task RefreshAsync() => QueueNavigation(() => NavigateCore(CurrentPath, null, null));

    private Task QueueNavigation(Func<Task> action)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = _navigation;
        _navigation = gate.Task;
        _ = Drain();
        return gate.Task;

        async Task Drain()
        {
            try { await previous.ConfigureAwait(false); } catch { /* A failed listing must not block the next one. */ }
            try
            {
                await RunOnUiAsync(action);
                gate.TrySetResult();
            }
            catch (Exception ex) { gate.TrySetException(ex); }
        }
    }

    private static Task RunOnUiAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess()) return action();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try { await action(); done.TrySetResult(); }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        return done.Task;
    }

    private async Task NavigateCore(string path, string? selectPath, string? renamedFrom)
    {
        EndRange();
        var normalized = Provider.NormalizePath(path);
        var same = Provider.PathComparer.Equals(CurrentPath, normalized);
        var marked = _rows.Where(x => x.Marked).Select(x => x.Entry.FullPath).ToHashSet(Provider.PathComparer);
        if (renamedFrom is not null && selectPath is not null && marked.Remove(renamedFrom))
            marked.Add(Provider.NormalizePath(selectPath));
        var previous = selectPath is not null ? Provider.NormalizePath(selectPath) : same ? CurrentEntry?.FullPath : null;
        var index = same ? _list.SelectedIndex : 0;
        var entries = await Provider.ListAsync(normalized);
        // Commit only after a successful listing; a bad path never replaces the visible pane state.
        CurrentPath = normalized; _path.Text = Provider.DisplayPath(normalized);
        if (ReferenceEquals(Provider, _homeProvider)) _localPath = normalized;
        UpdateLocationSelection();
        _rows = entries.Select(entry => new FileRow(entry) { Marked = same && marked.Contains(entry.FullPath) }).ToList();
        _list.ItemsSource = _rows;
        var selected = previous is null ? -1 : _rows.FindIndex(x => Provider.PathComparer.Equals(x.Entry.FullPath, previous));
        _list.SelectedIndex = _rows.Count == 0 ? -1 : selected >= 0 ? selected : Math.Clamp(index, 0, _rows.Count - 1);
        _anchor = Math.Max(0, _list.SelectedIndex);
        UpdateCount();
    }

    public void ResetPathText() => _path.Text = CurrentPath;
    public void MarkFiles(string mask, bool marked)
    {
        var patterns = mask.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var row in _rows.Where(r => !r.Entry.IsDirectory && r.Entry.Name != ".."))
            if (patterns.Any(pattern => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                    pattern == "*.*" ? "*" : pattern, row.Entry.Name, ignoreCase: true)))
                row.Marked = marked;
        UpdateCount();
    }

    public void InvertFileMarks()
    {
        foreach (var row in _rows.Where(r => !r.Entry.IsDirectory && r.Entry.Name != "..")) row.Marked = !row.Marked;
        UpdateCount();
    }

    public void FocusPath()
    {
        if (IsRemote) { _remote.FocusSearch(); return; }
        _path.Focus(); _path.SelectAll();
    }
    public FileEntry? CurrentEntry => IsRemote ? null : (_list.SelectedItem as FileRow)?.Entry;
    public IReadOnlyList<FileEntry> OperationEntries
    {
        get
        {
            if (IsRemote) return [];
            var marked = _rows.Where(x => x.Marked && x.Entry.Name != "..").Select(x => x.Entry).ToArray();
            return marked.Length > 0 ? marked : CurrentEntry is { Name: not ".." } current ? [current] : [];
        }
    }

    public void FocusList()
    {
        if (IsRemote) { _remote.FocusList(); return; }
        var index = _list.SelectedIndex;
        _list.Focus();
        _list.SelectedIndex = index;
        if (index >= 0)
        {
            _list.ScrollIntoView(index);
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsEffectivelyEnabled || _list.SelectedIndex != index) return;
                if (TopLevel.GetTopLevel(this) is Window owner && owner.OwnedWindows.Any()) return;
                _list.ContainerFromIndex(index)?.Focus();
                _list.SelectedIndex = index;
            }, DispatcherPriority.Input);
        }
    }

    private void ListKeyDown(object? sender, KeyEventArgs e)
    {
        var index = _list.SelectedIndex;
        if (index < 0) return;
        if (e.Key == Key.Insert)
        {
            EndRange();
            Toggle(index);
            _list.SelectedIndex = Math.Min(index + 1, _rows.Count - 1);
            _anchor = _list.SelectedIndex;
            FocusList(); e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var next = Math.Clamp(index + (e.Key == Key.Up ? -1 : 1), 0, _rows.Count - 1);
            MarkRange(_anchor, next);
            _list.SelectedIndex = next; FocusList(); e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down or Key.Home or Key.End && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            EndRange();
            _anchor = e.Key switch { Key.Home => 0, Key.End => _rows.Count - 1, Key.Up => Math.Max(0, index - 1), _ => Math.Min(_rows.Count - 1, index + 1) };
        }
        else if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            EndRange();
            Toggle(index); e.Handled = true;
        }
    }

    private void ListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragPress = null; _dragRow = null;
        if (e.Source is not Visual source) return;
        var container = source as ListBoxItem ?? source.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        var properties = e.GetCurrentPoint(_list).Properties;
        if (properties.IsRightButtonPressed)
        {
            _systemMenuGesture = OperatingSystem.IsWindows() && e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            EndRange();
            Activated?.Invoke(this);
            if (container?.DataContext is FileRow clicked)
            {
                ConfigureContextMenu(true);
                if (!clicked.Marked) foreach (var item in _rows) item.Marked = false;
                _list.SelectedIndex = _rows.IndexOf(clicked);
                UpdateCount();
            }
            else ConfigureContextMenu(false);
            return;
        }
        if (!properties.IsLeftButtonPressed) return;
        if (container?.DataContext is not FileRow row) return;
        var index = _rows.IndexOf(row);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            EndRange();
            Toggle(index); _list.SelectedIndex = index; _anchor = index; FocusList(); e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            MarkRange(_anchor, index); _list.SelectedIndex = index; FocusList(); e.Handled = true;
        }
        else
        {
            EndRange();
            _anchor = index;
        }
        if (row.Entry.Name != "..") { _dragPress = e; _dragStart = e.GetPosition(_list); _dragRow = row; }
    }

    internal IReadOnlyList<FileEntry> EntriesForDrag(FileEntry entry)
    {
        var row = _rows.FirstOrDefault(r => Provider.PathComparer.Equals(r.Entry.FullPath, entry.FullPath));
        return row is null || entry.Name == ".." ? [] : row.Marked
            ? _rows.Where(r => r.Marked && r.Entry.Name != "..").Select(r => r.Entry).ToArray() : [entry];
    }

    private async void DragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragPress is not { } press || _dragRow is not { } row || IsDragging) return;
        if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed) { _dragPress = null; return; }
        var delta = e.GetPosition(_list) - _dragStart;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;
        _dragPress = null; _dragRow = null;
        var entries = EntriesForDrag(row.Entry);
        if (entries.Count == 0) return;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(DragFormat, new DraggedEntries(this, entries)));
            await AddLocalDragItemsAsync(data, entries);
            IsDragging = true;
            var result = await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy | DragDropEffects.Move);
            if (result == DragDropEffects.Move) await RefreshAsync();
        }
        catch (Exception ex) { InteractionError?.Invoke(ex.Message); }
        finally { IsDragging = false; ClearDropHighlight(); }
    }

    internal async Task AddLocalDragItemsAsync(DataTransfer data, IReadOnlyList<FileEntry> entries)
    {
        if (!Provider.SupportsLocalFileDrop) return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;
        foreach (var entry in entries)
        {
            if (entry.Name == ".." || string.IsNullOrWhiteSpace(entry.FullPath)) continue;
            IStorageItem? item = entry.IsDirectory
                ? await storage.TryGetFolderFromPathAsync(entry.FullPath)
                : await storage.TryGetFileFromPathAsync(entry.FullPath);
            item ??= entry.IsDirectory
                ? await storage.TryGetFileFromPathAsync(entry.FullPath)
                : await storage.TryGetFolderFromPathAsync(entry.FullPath);
            if (item is not null) data.Add(DataTransferItem.Create(DataFormat.File, item));
        }
    }

    internal bool CanAcceptDrop(DraggedEntries payload, string directory) => IsEffectivelyEnabled &&
        payload.Source != this && payload.Entries.Count > 0 &&
        !payload.Entries.Any(entry => entry.Name == ".." || LandsInsideSource(directory, entry));

    private bool LandsInsideSource(string directory, FileEntry entry)
    {
        if (!Provider.CanHandle(entry.FullPath)) return false;
        try { return Provider.IsSameOrDescendant(Provider.CombinePath(directory, entry.Name), entry.FullPath); }
        catch (ArgumentException) { return true; }
    }

    private string DropDirectory(DragEventArgs e)
    {
        var visual = _list.InputHitTest(e.GetPosition(_list)) as Visual;
        var row = visual as ListBoxItem ?? visual?.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (row?.DataContext is FileRow { Entry: { IsDirectory: true, IsLink: false } } item)
        {
            _dropRow = row;
            return item.Entry.FullPath;
        }
        return CurrentPath;
    }

    private void ClearDropHighlight()
    {
        _frame.Classes.Remove("drop-target");
        _dropRow?.Classes.Remove("drop-target"); _dropRow = null;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        ClearDropHighlight();
        var directory = DropDirectory(e);
        var payload = e.DataTransfer.TryGetValue(DragFormat);
        var allowed = payload is not null && CanAcceptDrop(payload, directory);
        var external = payload is null && IsEffectivelyEnabled &&
            e.DataTransfer.Contains(DataFormat.File) && e.DragEffects.HasFlag(DragDropEffects.Copy);
        allowed |= external;
        e.DragEffects = !allowed ? DragDropEffects.None : !external && e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? DragDropEffects.Move : DragDropEffects.Copy;
        if (allowed) { _frame.Classes.Add("drop-target"); _dropRow?.Classes.Add("drop-target"); }
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var payload = e.DataTransfer.TryGetValue(DragFormat);
        var directory = DropDirectory(e);
        ClearDropHighlight();
        if (payload is null)
        {
            e.Handled = true;
            var copyAllowed = e.DragEffects.HasFlag(DragDropEffects.Copy);
            e.DragEffects = DragDropEffects.None;
            if (!IsEffectivelyEnabled || !copyAllowed) return;
            try
            {
                var files = e.DataTransfer.TryGetFiles()?.ToArray();
                if (files is null || files.Length == 0) return;
                var paths = files.Select(file => file.TryGetLocalPath()).ToArray();
                if (paths.Any(path => string.IsNullOrWhiteSpace(path)))
                    throw new IOException("Only local files and folders can be dropped here.");
                var snapshot = paths.Select(path => path!).ToArray();
                e.DragEffects = DragDropEffects.Copy;
                Dispatcher.UIThread.Post(() => RequestExternalDrop(snapshot, directory), DispatcherPriority.Background);
            }
            catch (Exception ex) { InteractionError?.Invoke(ex.Message); }
            return;
        }
        if (payload is null || !CanAcceptDrop(payload, directory)) { e.DragEffects = DragDropEffects.None; return; }
        var move = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        e.DragEffects = move ? DragDropEffects.Move : DragDropEffects.Copy; e.Handled = true;
        // Let the native drag loop finish before showing confirmation.
        Dispatcher.UIThread.Post(() => RequestDrop(payload, directory, move), DispatcherPriority.Background);
    }

    internal void RequestDrop(DraggedEntries payload, string directory, bool move)
    {
        if (CanAcceptDrop(payload, directory)) DropRequested?.Invoke(this, payload, directory, move);
    }

    internal void RequestExternalDrop(IReadOnlyList<string> paths, string directory)
    {
        if (IsEffectivelyEnabled && paths.Count > 0)
            ExternalDropRequested?.Invoke(this, paths, directory);
    }

    private void Toggle(int index)
    {
        if (_rows[index].Entry.Name != "..") _rows[index].Marked = !_rows[index].Marked;
        UpdateCount();
    }

    private void EndRange() => _rangeMarkBase = null;

    // Marks from Insert stay. The Shift-range itself is replaced when it grows or shrinks.
    private void MarkRange(int from, int to)
    {
        _rangeMarkBase ??= _rows.Where(row => row.Marked).Select(row => row.Entry.FullPath).ToHashSet(Provider.PathComparer);
        var start = Math.Min(from, to);
        var end = Math.Max(from, to);
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Entry.Name == "..") continue;
            _rows[i].Marked = _rangeMarkBase.Contains(_rows[i].Entry.FullPath) || (i >= start && i <= end);
        }
        UpdateCount();
    }

    private bool UsesSessionBar() =>
        _openSessions.Count > 0 && (IsRemote || (_remoteProvider is not null && !ReferenceEquals(Provider, _homeProvider)));

    private void RememberSessionPath()
    {
        if (ActiveSessionId() is not { } id) return;
        if (!ReferenceEquals(Provider, _homeProvider)) _sessionPaths[id] = CurrentPath;
        else if (_remotePath is not null) _sessionPaths[id] = _remotePath;
    }

    private string? ActiveSessionId() =>
        _remoteProvider is null ? null
        : _openSessions.FirstOrDefault(session => ReferenceEquals(session.Provider, _remoteProvider))?.Id;

    private void RenderSessions()
    {
        _locations.Children.Clear();
        _locationButtons.Clear();
        var active = ActiveSessionId();
        foreach (var session in _openSessions)
        {
            var caption = new TextBlock
            {
                Name = "OpenSessionLabel-" + session.Id,
                Text = SessionCaption(session), MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis
            };
            var button = new Button
            {
                Name = "OpenSession-" + session.Id,
                Content = caption,
                Padding = new Thickness(9, 4),
                MinHeight = 28
            };
            button.Classes.Add("flat");
            if (session.Link == SessionLink.Disconnected) button.Classes.Add("session-off");
            if (session.Id == active) button.Classes.Add("chosen");
            Avalonia.Automation.AutomationProperties.SetName(button, SessionCaption(session));
            ToolTip.SetTip(button, session.Link switch
            {
                SessionLink.Reconnecting => "Reconnecting…",
                SessionLink.Disconnected => "Disconnected. The profile is kept. Click to reconnect.",
                _ => "Connected. Right-click to reconnect, disconnect, edit, or close."
            });
            var id = session.Id;
            button.Click += (_, _) => { Activated?.Invoke(this); SessionChosen?.Invoke(this, id); };
            var menu = new ContextMenu();
            menu.Items.Add(SessionMenu("Reconnect", () => SessionReconnectRequested?.Invoke(this, id)));
            if (session.Link == SessionLink.Connected)
                menu.Items.Add(SessionMenu("Disconnect", () => SessionDisconnectRequested?.Invoke(this, id)));
            menu.Items.Add(SessionMenu("Edit", () => SessionEditRequested?.Invoke(this, id)));
            menu.Items.Add(SessionMenu("Forget sign-in", () => SessionForgetRequested?.Invoke(this, id)));
            menu.Items.Add(SessionMenu("Close", () => SessionCloseRequested?.Invoke(this, id)));
            button.ContextMenu = menu;
            _locations.Children.Add(button);
            _locationButtons.Add((SessionPathKey(id), button));
        }
        ArrangeLocations();
    }

    private static string SessionCaption(OpenSessionChip session) => session.Link switch
    {
        SessionLink.Reconnecting => session.Name + " · Reconnecting",
        SessionLink.Disconnected => session.Name + " · Disconnected",
        _ => session.Name
    };

    private static MenuItem SessionMenu(string caption, Action invoke)
    {
        var item = new MenuItem { Header = caption };
        item.Click += (_, _) => invoke();
        return item;
    }

    private void ClearLocationButtons()
    {
        _locations.Children.Clear();
        _locationButtons.Clear();
        _moreLocations.IsVisible = false;
        _moreLocations.ContextMenu = null;
    }

    private static bool IsSessionPath(string path) => path.StartsWith("session:", StringComparison.Ordinal);

    private static string SessionPathKey(string id) => "session:" + id;

    private static string? SessionId(string path) => IsSessionPath(path) ? path["session:".Length..] : null;

    private void UpdateLocationSelection()
    {
        if (_locationButtons.Any(item => IsSessionPath(item.Path)))
        {
            var active = ActiveSessionId();
            foreach (var (path, button) in _locationButtons)
                button.Classes.Set("chosen", active is not null && path == SessionPathKey(active));
            _moreLocations.Classes.Set("chosen", _locationButtons.Any(item => !item.Button.IsVisible && item.Button.Classes.Contains("chosen")));
            return;
        }
        var best = _locationButtons.Where(item => Provider.IsSameOrDescendant(CurrentPath, item.Path))
            .OrderByDescending(item => item.Path.Length).FirstOrDefault().Path;
        foreach (var (path, button) in _locationButtons)
            button.Classes.Set("chosen", path == best);
        _moreLocations.Classes.Set("chosen", _locationButtons.Any(item => !item.Button.IsVisible && item.Button.Classes.Contains("chosen")));
    }

    private void UpdateCount()
    {
        var folders = _rows.Count(x => x.Entry.Name != ".." && x.Entry.IsDirectory);
        var files = _rows.Count(x => !x.Entry.IsDirectory);
        var marked = _rows.Count(x => x.Marked);
        _count.Text = $"{folders} folders · {files} files" + (marked > 0 ? $"   /   {marked} marked" : "");
    }
}
