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
using DockCommander.Core.Files;

namespace DockCommander.App;

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
    private readonly TextBlock _count = new() { FontSize = 12 };
    private readonly TextBlock _activity = new() { FontSize = 10, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly string _label;
    private readonly List<(string Path, Button Button)> _locationButtons = [];
    private List<FileRow> _rows = [];
    private int _anchor;
    private HashSet<string>? _rangeMarkBase;
    public IFileSystemProvider Provider { get; }
    public string CurrentPath { get; private set; }
    public event Action<FilePane>? Activated;
    public event Action<FilePane, string>? NavigateRequested;
    public event Action<FilePane>? OpenRequested;

    public FilePane(IFileSystemProvider provider, string path, string label = "FILES")
    {
        Provider = provider; CurrentPath = path; _label = label;
        _list.Classes.Add("file-list");
        _path.Classes.Add("path-field");
        _count.Classes.Add("muted");
        _activity.Classes.Add("muted");
        _activity.Text = label;
        ToolTip.SetTip(_path, "Current path · Ctrl+L to edit");
        _list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _path.Text = path;
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var locationsRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _locationHost.Children.Add(new Border { Child = _locations, ClipToBounds = true });
        _moreLocations.Classes.Add("flat");
        Grid.SetColumn(_moreLocations, 1); _locationHost.Children.Add(_moreLocations);
        _moreLocations.Click += (_, _) => _moreLocations.ContextMenu?.Open(_moreLocations);
        _locationHost.SizeChanged += (_, _) => ArrangeLocations();
        locationsRow.Children.Add(_locationHost);
        Grid.SetColumn(_activity, 1); locationsRow.Children.Add(_activity);
        var top = new StackPanel(); top.Children.Add(locationsRow); top.Children.Add(_path);
        var topBorder = new Border { Child = top }; topBorder.Classes.Add("pane-top"); Add(topBorder, 0);
        var header = new Border { Child = Row("", "Name", "Type", "Size", "Modified", true) };
        header.Classes.Add("column-header"); Add(header, 1);
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
        Add(_list, 2);
        var footer = new Border { Child = _count }; footer.Classes.Add("pane-footer"); Add(footer, 3);
        _frame = new Border { Child = grid }; _frame.Classes.Add("pane-frame");
        Content = _frame;
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
                     ("Copy to other panel · F5", "copy"), ("Move to other panel · F6", "move"),
                     ("Rename · Shift+F6", "rename"), ("New folder · F7", "mkdir"),
                     ("Recycle · F8", "recycle"), ("Refresh · Ctrl+R", "refresh") })
        {
            var item = new MenuItem { Header = caption, Name = "Context_" + action };
            item.Click += (_, _) => ActionRequested?.Invoke(this, action);
            menu.Items.Add(item);
        }
        _list.ContextMenu = menu;
        _list.ContextRequested += (_, e) =>
        {
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
            item.IsEnabled = onRow || action is "mkdir" or "refresh";
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

    public void SetActive(bool active)
    {
        _frame.Classes.Set("active", active);
        _activity.Classes.Set("accent", active);
        _activity.Text = active ? $"{_label} · ACTIVE" : _label;
    }

    public async Task LoadLocationsAsync()
    {
        SetLocations(await Provider.GetLocationsAsync());
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
                var item = new MenuItem { Header = $"{Avalonia.Automation.AutomationProperties.GetName(button)} — {path}" };
                item.Click += (_, _) => { Activated?.Invoke(this); NavigateRequested?.Invoke(this, path); };
                menu.Items.Add(item);
            }
        }
        _moreLocations.IsVisible = menu.Items.Count > 0;
        _moreLocations.ContextMenu = menu;
        _moreLocations.Classes.Set("chosen", _locationButtons.Any(item => !item.Button.IsVisible && item.Button.Classes.Contains("chosen")));
    }

    public async Task NavigateAsync(string path, string? selectPath = null, string? renamedFrom = null)
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
        CurrentPath = normalized; _path.Text = normalized;
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

    public void FocusPath() { _path.Focus(); _path.SelectAll(); }
    public FileEntry? CurrentEntry => (_list.SelectedItem as FileRow)?.Entry;
    public IReadOnlyList<FileEntry> OperationEntries
    {
        get
        {
            var marked = _rows.Where(x => x.Marked && x.Entry.Name != "..").Select(x => x.Entry).ToArray();
            return marked.Length > 0 ? marked : CurrentEntry is { Name: not ".." } current ? [current] : [];
        }
    }

    public void FocusList()
    {
        var index = _list.SelectedIndex;
        _list.Focus();
        _list.SelectedIndex = index;
        if (index >= 0)
        {
            _list.ScrollIntoView(index);
            Dispatcher.UIThread.Post(() =>
            {
                if (IsEffectivelyEnabled && _list.SelectedIndex == index)
                {
                    _list.ContainerFromIndex(index)?.Focus();
                    _list.SelectedIndex = index;
                }
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
        var data = new DataTransfer(); data.Add(DataTransferItem.Create(DragFormat, new DraggedEntries(this, entries)));
        IsDragging = true;
        try { await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy | DragDropEffects.Move); }
        catch (Exception ex) { InteractionError?.Invoke(ex.Message); }
        finally { IsDragging = false; ClearDropHighlight(); }
    }

    internal bool CanAcceptDrop(DraggedEntries payload, string directory) => IsEffectivelyEnabled &&
        payload.Source != this && ReferenceEquals(payload.Source.Provider, Provider) && payload.Entries.Count > 0 &&
        !payload.Entries.Any(entry => entry.Name == ".." ||
            Provider.IsSameOrDescendant(Provider.CombinePath(directory, entry.Name), entry.FullPath));

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
        var external = payload is null && IsEffectivelyEnabled && Provider.SupportsLocalFileDrop &&
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
            if (!IsEffectivelyEnabled || !Provider.SupportsLocalFileDrop || !copyAllowed) return;
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
        if (IsEffectivelyEnabled && Provider.SupportsLocalFileDrop && paths.Count > 0)
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

    private void UpdateLocationSelection()
    {
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
