using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DockCommander.Core.Files;

namespace DockCommander.App;

internal sealed class TextViewerWindow : Window
{
    public TextViewerWindow(Window owner, FileEntry entry, TextPreview preview)
    {
        Title = $"{entry.Name} — View";
        RequestedThemeVariant = owner.ActualThemeVariant;
        Icon = owner.Icon;
        Width = Math.Min(960, owner.Width); Height = Math.Min(700, owner.Height);
        MinWidth = 500; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(16) };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        var title = new TextBlock { Text = entry.Name, FontSize = 18, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(title, entry.FullPath); header.Children.Add(title);
        var wrap = new CheckBox { Content = "Wrap lines", Name = "WrapLines", Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(wrap, 1); header.Children.Add(wrap); root.Children.Add(header);
        var text = new TextBox
        {
            Name = "PreviewText", Text = preview.Text, IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 13, Padding = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        text.Classes.Add("path-field");
        text.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        text.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        wrap.IsCheckedChanged += (_, _) =>
        {
            var enabled = wrap.IsChecked == true;
            text.TextWrapping = enabled ? TextWrapping.Wrap : TextWrapping.NoWrap;
            text.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, enabled ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        };
        Grid.SetRow(text, 1); root.Children.Add(text);
        var footer = new TextBlock
        {
            Text = $"{preview.EncodingName} · Read only · Ctrl+A / Ctrl+C to copy · Esc to close" +
                (preview.IsTruncated ? " · Showing only the first 256 KiB" : ""),
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 0)
        };
        footer.Classes.Add("muted"); Grid.SetRow(footer, 2); root.Children.Add(footer);
        Content = root;
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Escape or Key.F3) { e.Handled = true; Close(); }
        }, RoutingStrategies.Tunnel);
        Opened += (_, _) => { text.Focus(); text.CaretIndex = 0; };
    }
}
