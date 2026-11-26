using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DockCommander.Core.Files;

namespace DockCommander.App;

internal sealed class TextEditorWindow : Window
{
    private readonly FileEntry _entry;
    private readonly IFileSystemProvider _provider;
    private readonly TextBox _text;
    private readonly TextBox _search;
    private readonly Control _searchBar;
    private readonly TextBlock _position;
    private readonly TextBlock _status;
    private readonly ComboBox _encoding;
    private readonly Button _lineEnding;
    private byte[] _bytes;
    private string _savedText;
    private string _encodingId;
    private string _savedEncoding;
    private string _newLine;
    private string _savedNewLine;
    private bool _allowClose;
    private bool _prompting;

    public bool DidSave { get; private set; }
    public bool IsDirty => (_text.Text ?? "") != _savedText || _encodingId != _savedEncoding || _newLine != _savedNewLine;

    public TextEditorWindow(Window owner, FileEntry entry, IFileSystemProvider provider, TextDocument document)
    {
        _entry = entry;
        _provider = provider;
        _bytes = document.Bytes;
        _encodingId = _savedEncoding = document.EncodingId;
        _newLine = _savedNewLine = document.NewLine;
        Title = $"{entry.Name} — Editing";
        Owner = owner;
        RequestedThemeVariant = owner.ActualThemeVariant;
        Icon = owner.Icon;
        Width = Math.Min(960, owner.Width); Height = Math.Min(700, owner.Height);
        MinWidth = 560; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(16) };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        var title = new TextBlock
        {
            Text = entry.Name, FontSize = 18, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(title, entry.FullPath);
        header.Children.Add(title);
        var save = new Button { Name = "SaveButton", Content = "Save", MinWidth = 72, Margin = new Thickness(12, 0, 0, 0) };
        save.Classes.Add("primary");
        save.Click += async (_, _) => await SaveAsync();
        Grid.SetColumn(save, 1); header.Children.Add(save);
        var wrap = new CheckBox { Content = "Wrap lines", Name = "WrapLines", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(wrap, 2); header.Children.Add(wrap);
        root.Children.Add(header);

        _search = new TextBox { Name = "SearchText", PlaceholderText = "Find", MinWidth = 180 };
        _search.Classes.Add("path-field");
        var previous = new Button { Name = "SearchPrevious", Content = "Previous", MinWidth = 80 };
        var next = new Button { Name = "SearchNext", Content = "Next", MinWidth = 72 };
        previous.Click += (_, _) => Find(false);
        next.Click += (_, _) => Find(true);
        _search.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Find(!e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        };
        var searchBar = new StackPanel
        {
            Name = "SearchBar", Orientation = Orientation.Horizontal, Spacing = 8,
            Margin = new Thickness(0, 0, 0, 8), IsVisible = false,
            Children = { _search, previous, next }
        };
        _searchBar = searchBar;
        Grid.SetRow(searchBar, 1); root.Children.Add(searchBar);

        _text = new TextBox
        {
            Name = "EditorText", Text = document.Text, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"), FontSize = 13,
            Padding = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _text.Classes.Add("path-field");
        _savedText = _text.Text ?? "";
        _text.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        _text.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        wrap.IsCheckedChanged += (_, _) =>
        {
            var enabled = wrap.IsChecked == true;
            _text.TextWrapping = enabled ? TextWrapping.Wrap : TextWrapping.NoWrap;
            _text.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, enabled ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        };
        _text.TextChanged += (_, _) => { UpdateDirty(); UpdateCaret(); };
        _text.PropertyChanged += (_, e) => { if (e.Property == TextBox.CaretIndexProperty) UpdateCaret(); };
        Grid.SetRow(_text, 2); root.Children.Add(_text);

        _position = new TextBlock { Name = "CaretPosition", Text = "Ln 1, Col 1", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        _position.Classes.Add("muted");
        _encoding = new ComboBox
        {
            Name = "Encoding", ItemsSource = TextDocuments.Encodings, MinWidth = 140,
            SelectedItem = TextDocuments.Encodings.First(choice => choice.Id == document.EncodingId)
        };
        _encoding.SelectionChanged += (_, _) =>
        {
            if (_encoding.SelectedItem is not TextEncodingChoice choice || choice.Id == _encodingId) return;
            _encodingId = choice.Id;
            UpdateDirty();
            SetMessage($"Save uses {choice.Name}.");
        };
        var reload = new Button { Name = "ReloadButton", Content = "Reload", MinWidth = 72 };
        reload.Click += async (_, _) => await ReloadAsync();
        _lineEnding = new Button { Name = "LineEndingButton", Content = TextDocuments.NewLineLabel(_newLine), MinWidth = 64 };
        _lineEnding.Click += (_, _) =>
        {
            _newLine = _newLine == "\n" ? "\r\n" : "\n";
            _lineEnding.Content = TextDocuments.NewLineLabel(_newLine);
            UpdateDirty();
        };
        _status = new TextBlock { Name = "EditorStatus", FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _status.Classes.Add("muted");
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*"), Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(_position);
        var encodingHost = new Border { Child = _encoding, Margin = new Thickness(16, 0, 0, 0) };
        Grid.SetColumn(encodingHost, 1); footer.Children.Add(encodingHost);
        var reloadHost = new Border { Child = reload, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(reloadHost, 2); footer.Children.Add(reloadHost);
        var endingHost = new Border { Child = _lineEnding, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(endingHost, 3); footer.Children.Add(endingHost);
        var statusHost = new Border { Child = _status, Margin = new Thickness(16, 0, 0, 0) };
        Grid.SetColumn(statusHost, 4); footer.Children.Add(statusHost);
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root;
        if (document.LowConfidence) SetMessage("Encoding was guessed. Choose another one if the text looks wrong.");

        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                e.Handled = true;
                _ = SaveAsync();
                return;
            }
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                e.Handled = true;
                _searchBar.IsVisible = true;
                _search.Focus();
                _search.SelectAll();
                return;
            }
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (_searchBar.IsVisible)
            {
                _searchBar.IsVisible = false;
                _text.Focus();
                return;
            }
            Close();
        }, RoutingStrategies.Tunnel);
        Closing += OnClosing;
        Opened += (_, _) =>
        {
            _savedText = _text.Text ?? "";
            UpdateDirty();
            UpdateCaret();
            _text.Focus();
            _text.CaretIndex = 0;
        };
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose || !IsDirty) return;
        e.Cancel = true;
        if (_prompting) return;
        _prompting = true;
        try
        {
            var choice = await Dialogs.AskSaveAsync(this, _entry.Name);
            if (choice == SaveChoice.Cancel) return;
            if (choice == SaveChoice.Discard || await SaveAsync())
            {
                _allowClose = true;
                Close();
            }
        }
        finally { _prompting = false; }
    }

    private async Task<bool> SaveAsync()
    {
        try
        {
            var text = _text.Text ?? "";
            var encoding = _encodingId;
            var newLine = _newLine;
            var bytes = TextDocuments.Encode(text, encoding, newLine);
            await using var stream = new MemoryStream(bytes, writable: false);
            await _provider.WriteFileAsync(_entry.FullPath, stream, true, null, CancellationToken.None);
            _bytes = bytes;
            _savedText = text;
            _savedEncoding = encoding;
            _savedNewLine = newLine;
            DidSave = true;
            UpdateDirty();
            SetMessage("Saved.");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetMessage(ex.Message, error: true);
            return false;
        }
        catch (OperationCanceledException)
        {
            SetMessage("The save was cancelled.", error: true);
            return false;
        }
    }

    private async Task ReloadAsync()
    {
        if (IsDirty)
        {
            SetMessage("Save or discard edits before reloading another encoding.", error: true);
            return;
        }
        try
        {
            var document = await TextDocuments.ReadAsync(_provider, _entry.FullPath, TextDocuments.MaxEditBytes, false, CancellationToken.None);
            document = TextDocuments.Decode(document.Bytes, encodingId: _encodingId);
            _bytes = document.Bytes;
            _text.Text = document.Text;
            _savedText = _text.Text ?? "";
            _newLine = _savedNewLine = document.NewLine;
            _lineEnding.Content = TextDocuments.NewLineLabel(_newLine);
            UpdateDirty();
            UpdateCaret();
            SetMessage("Reloaded.");
        }
        catch (Exception ex)
        {
            SetMessage(ex.Message, error: true);
        }
    }

    private void Find(bool forward)
    {
        var needle = _search.Text ?? "";
        var haystack = _text.Text ?? "";
        if (needle.Length == 0)
        {
            SetMessage("Enter text to find.");
            return;
        }
        var index = forward ? NextIndex(haystack, needle) : PreviousIndex(haystack, needle);
        if (index < 0)
        {
            SetMessage("No matches.");
            return;
        }
        _text.CaretIndex = index + needle.Length;
        _text.SelectionStart = index;
        _text.SelectionEnd = index + needle.Length;
        _text.Focus();
        SetMessage("");
    }

    private int NextIndex(string haystack, string needle)
    {
        var start = Math.Clamp(Math.Max(_text.SelectionStart, _text.SelectionEnd), 0, haystack.Length);
        var index = haystack.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
        return index >= 0 || start == 0 ? index : haystack.IndexOf(needle, 0, StringComparison.OrdinalIgnoreCase);
    }

    private int PreviousIndex(string haystack, string needle)
    {
        var start = Math.Min(_text.SelectionStart, _text.SelectionEnd);
        if (start > 0)
        {
            var index = haystack.LastIndexOf(needle, start - 1, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) return index;
        }
        return haystack.LastIndexOf(needle, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateCaret()
    {
        var content = _text.Text ?? "";
        var index = Math.Clamp(_text.CaretIndex, 0, content.Length);
        var line = 1;
        var column = 1;
        for (var i = 0; i < index; i++)
        {
            if (content[i] == '\n') { line++; column = 1; }
            else if (content[i] != '\r') column++;
        }
        _position.Text = $"Ln {line}, Col {column}";
    }

    private void UpdateDirty() => Title = IsDirty ? $"{_entry.Name} — Editing •" : $"{_entry.Name} — Editing";

    private void SetMessage(string message, bool error = false)
    {
        _status.Text = message;
        _status.Classes.Set("error", error);
        _status.Classes.Set("muted", !error && message.Length > 0);
    }
}
