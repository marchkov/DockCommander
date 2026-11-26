using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using DockCommander.Core.Connections;

namespace DockCommander.App;

internal sealed class RemotePaneView : UserControl
{
    private readonly TextBox _search = new() { Name = "SearchConnections", PlaceholderText = "Search connections..." };
    private readonly ListBox _list = new() { Name = "ConnectionList" };
    private readonly TextBlock _empty = new() { Text = "No saved connections.", FontSize = 13, Margin = new Thickness(4, 12, 0, 0) };
    private IReadOnlyList<ConnectionProfile> _connections = [];

    public event Action? AddRequested;
    public event Action<ConnectionProfile>? EditRequested;
    public event Action<ConnectionProfile>? ConnectRequested;

    public RemotePaneView()
    {
        _search.Classes.Add("path-field");
        _list.Classes.Add("file-list");
        _empty.Classes.Add("muted");
        _list.ItemTemplate = new FuncDataTemplate<ConnectionProfile>((profile, _) =>
        {
            if (profile is null) return new TextBlock();
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                Margin = new Thickness(6, 0, 10, 0),
                MinHeight = 36
            };
            var text = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = profile.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            var detail = new TextBlock
            {
                Text = profile.Detail, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            detail.Classes.Add("muted");
            text.Children.Add(detail);
            row.Children.Add(text);
            var edit = Action("Edit", () => EditRequested?.Invoke(profile));
            var connect = Action("Connect", () => ConnectRequested?.Invoke(profile));
            Grid.SetColumn(edit, 1); Grid.SetColumn(connect, 2);
            row.Children.Add(edit); row.Children.Add(connect);
            return row;
        });
        var add = new Button { Name = "AddConnection", Content = "Add connection", MinWidth = 132 };
        add.Classes.Add("flat");
        add.Click += (_, _) => AddRequested?.Invoke();
        var tools = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(10, 4, 10, 4) };
        tools.Children.Add(_search);
        Grid.SetColumn(add, 1); add.Margin = new Thickness(8, 0, 0, 0); tools.Children.Add(add);
        var toolsBorder = new Border { Child = tools };
        toolsBorder.Classes.Add("column-header");
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        body.Children.Add(toolsBorder);
        var listHost = new Grid();
        listHost.Children.Add(_list); listHost.Children.Add(_empty);
        Grid.SetRow(listHost, 1); body.Children.Add(listHost);
        Content = body;
        _search.TextChanged += (_, _) => ApplyFilter();
    }

    public void SetConnections(IReadOnlyList<ConnectionProfile> connections)
    {
        _connections = connections;
        ApplyFilter();
    }

    public void RefreshConnections() => ApplyFilter();

    public void FocusSearch() { _search.Focus(); _search.SelectAll(); }

    public void FocusList()
    {
        if (_list.ItemCount == 0) FocusSearch();
        else _list.Focus();
    }

    private void ApplyFilter()
    {
        var query = _search.Text?.Trim() ?? "";
        var visible = (string.IsNullOrEmpty(query) ? _connections : _connections.Where(profile =>
                profile.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                profile.Protocol.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                profile.Host.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                profile.Detail.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        _list.ItemsSource = visible;
        _empty.IsVisible = visible.Length == 0;
        _empty.Text = _connections.Count == 0 ? "No saved connections." : "No connections match this search.";
    }

    private static Button Action(string caption, Action invoke)
    {
        var button = new Button
        {
            Content = caption, MinWidth = 64, MinHeight = 0, Height = 24,
            Padding = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center
        };
        button.Classes.Add("flat");
        button.Click += (_, _) => invoke();
        return button;
    }
}
