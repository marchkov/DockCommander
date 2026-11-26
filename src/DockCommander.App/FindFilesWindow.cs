using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using DockCommander.Core.Files;

namespace DockCommander.App;

internal sealed class FindFilesWindow : Window
{
    private readonly IFileSystemProvider _provider;
    private readonly TextBox _pattern;
    private readonly TextBox _start;
    private readonly ListBox _results;
    private readonly TextBlock _status;
    private readonly ObservableCollection<SearchRow> _rows = [];
    private CancellationTokenSource? _search;
    private bool _searching;
    private bool _closed;

    private FindFilesWindow(Window owner, IFileSystemProvider provider, string startDisplay)
    {
        _provider = provider;
        Title = "Find files";
        Width = 720;
        Height = 520;
        MinWidth = 560;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RequestedThemeVariant = owner.ActualThemeVariant;
        Icon = owner.Icon;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = "Find files", FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        var form = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 8) };
        form.Children.Add(new TextBlock { Text = "Search for:" });
        _pattern = new TextBox { Name = "FindPattern", Text = "*", PlaceholderText = "*.txt" };
        _pattern.Classes.Add("path-field");
        _pattern.KeyDown += async (_, e) => await StartFromField(e);
        form.Children.Add(_pattern);
        form.Children.Add(new TextBlock { Text = "Start in:" });
        _start = new TextBox { Name = "FindStart", Text = startDisplay };
        _start.Classes.Add("path-field");
        _start.KeyDown += async (_, e) => await StartFromField(e);
        form.Children.Add(_start);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var search = new Button { Name = "FindSearch", Content = "Search", MinWidth = 88 };
        search.Classes.Add("primary");
        search.Click += async (_, _) => await SearchAsync();
        var cancel = new Button { Name = "FindCancel", Content = "Cancel", MinWidth = 88 };
        cancel.Click += (_, _) => CancelOrClose();
        buttons.Children.Add(search);
        buttons.Children.Add(cancel);
        form.Children.Add(buttons);
        Grid.SetRow(form, 1);
        root.Children.Add(form);
        var results = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var columns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(8, 0, 8, 4) };
        columns.Children.Add(Cell("Name", 180));
        columns.Children.Add(Cell("Path", 280));
        columns.Children.Add(Cell("Type", 72));
        columns.Children.Add(Cell("Size", 72));
        results.Children.Add(columns);
        _results = new ListBox { Name = "FindResults", ItemsSource = _rows };
        _results.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<SearchRow>((row, _) =>
        {
            if (row is null) return new TextBlock();
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(8, 4) };
            line.Children.Add(Cell(row.Name, 180));
            line.Children.Add(Cell(row.Path, 280));
            line.Children.Add(Cell(row.Type, 72));
            line.Children.Add(Cell(row.Size, 72));
            return line;
        });
        _results.DoubleTapped += (_, _) => Accept();
        _results.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Accept();
        };
        Grid.SetRow(_results, 1);
        results.Children.Add(_results);
        Grid.SetRow(results, 2);
        root.Children.Add(results);
        _status = new TextBlock { Name = "FindStatus", FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
        _status.Classes.Add("muted");
        Grid.SetRow(_status, 3);
        root.Children.Add(_status);
        Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            CancelOrClose();
        };
        Closed += (_, _) => { _closed = true; _search?.Cancel(); };
        Opened += (_, _) => { _pattern.Focus(); _pattern.SelectAll(); };
    }

    public static Task<FileSearchHit?> ShowAsync(Window owner, IFileSystemProvider provider, string startDisplay) =>
        new FindFilesWindow(owner, provider, startDisplay).ShowDialog<FileSearchHit?>(owner);

    private async Task SearchAsync()
    {
        var pattern = _pattern.Text?.Trim() ?? "";
        if (pattern.Length == 0)
        {
            SetStatus("Enter a file name or mask.");
            return;
        }
        string directory;
        try { directory = _provider.NormalizePath(_start.Text ?? ""); }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            return;
        }
        _search?.Cancel();
        var search = new CancellationTokenSource();
        _search = search;
        _rows.Clear();
        _searching = true;
        SetStatus("Searching…");
        FileSearchOutcome outcome;
        try
        {
            outcome = await FileSearch.SearchAsync(_provider, directory, pattern, hit =>
        {
            var row = new SearchRow(hit, _provider);
            void Add() { if (!_closed && ReferenceEquals(_search, search)) _rows.Add(row); }
            if (Dispatcher.UIThread.CheckAccess()) Add();
            else Dispatcher.UIThread.Post(Add);
            }, search.Token);
        }
        catch (Exception ex)
        {
            if (!_closed && ReferenceEquals(_search, search)) SetStatus(ex.Message);
            return;
        }
        finally
        {
            if (ReferenceEquals(_search, search)) _searching = false;
        }
        if (_closed || !ReferenceEquals(_search, search)) return;
        SetStatus(Describe(outcome.Hits.Count, outcome.SkippedDirectories, outcome.Cancelled));
    }

    private async Task StartFromField(KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchAsync();
    }

    private void Accept()
    {
        if (_results.SelectedItem is SearchRow row) Close(row.Hit);
    }

    private void CancelOrClose()
    {
        if (_searching && _search is { IsCancellationRequested: false } search)
        {
            search.Cancel();
            return;
        }
        Close(null);
    }

    private void SetStatus(string text) => _status.Text = text;

    private static string Describe(int results, int skipped, bool cancelled)
    {
        var text = $"{results} {(results == 1 ? "result" : "results")}";
        if (skipped > 0) text += $" · {skipped} {(skipped == 1 ? "folder" : "folders")} skipped";
        return cancelled ? "Cancelled. " + text : text;
    }

    private static TextBlock Cell(string text, double width) => new()
    {
        Text = text, Width = width, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };

    internal sealed class SearchRow
    {
        public SearchRow(FileSearchHit hit, IFileSystemProvider provider)
        {
            Hit = hit;
            Name = hit.Name;
            Path = provider.DisplayPath(hit.FullPath);
            Type = hit.IsDirectory ? "Folder" : string.IsNullOrEmpty(System.IO.Path.GetExtension(hit.Name))
                ? "File" : System.IO.Path.GetExtension(hit.Name).TrimStart('.');
            Size = hit.IsDirectory || hit.Size is not long size ? "" : FilePane.FormatSize(size);
        }

        public FileSearchHit Hit { get; }
        public string Name { get; }
        public string Path { get; }
        public string Type { get; }
        public string Size { get; }
    }
}
