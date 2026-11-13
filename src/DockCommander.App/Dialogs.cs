using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Automation;
using DockCommander.Core.Operations;
using DockCommander.Core.Files;

namespace DockCommander.App;

internal static class Dialogs
{
    public static async Task<LocationEntry?> ChooseLocationAsync(Window owner, string title,
        IReadOnlyList<LocationEntry> locations, int selectedIndex)
    {
        var (window, body, buttons) = Create(owner, title, "Choose a location. Enter opens it; Esc cancels.");
        var list = new ListBox { Name = "LocationList", MaxHeight = 320, Focusable = true,
            ItemsSource = locations.Select(l => $"{l.DisplayName}  —  {l.RootPath}").ToArray(),
            SelectedIndex = locations.Count == 0 ? -1 : Math.Clamp(selectedIndex, 0, locations.Count - 1) };
        body.Children.Add(list);
        var ok = new Button { Content = "Open", IsDefault = true, IsEnabled = locations.Count > 0, MinWidth = 90 };
        ok.Classes.Add("primary");
        void Accept() { if (list.SelectedIndex >= 0) window.Close(locations[list.SelectedIndex]); }
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        cancel.Click += (_, _) => window.Close();
        buttons.Children.Add(ok); buttons.Children.Add(cancel); body.Children.Add(buttons);
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; window.Close(); } };
        window.Opened += (_, _) => list.Focus();
        return await window.ShowDialog<LocationEntry?>(owner);
    }

    private static (Window Window, StackPanel Body, StackPanel Buttons) Create(Window owner, string title, string message)
    {
        var body = new StackPanel { Spacing = 16, Margin = new Thickness(24) };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Background = Brushes.Transparent };
        header.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(header);
        var description = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 512, LineHeight = 21 };
        description.Classes.Add("muted"); body.Children.Add(description);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var window = new Window
        {
            Title = title, Width = 560, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = body, ShowInTaskbar = false,
            RequestedThemeVariant = owner.ActualThemeVariant, Icon = owner.Icon,
            WindowDecorations = WindowDecorations.None, ExtendClientAreaToDecorationsHint = true
        };
        var close = new Button { Name = "DialogCloseButton", Content = UiIcons.Create("close", size: 14),
            Width = 34, Height = 34, Margin = new Thickness(12, 0, 0, 0) };
        close.Classes.Add("flat"); close.Classes.Add("caption-close");
        AutomationProperties.SetName(close, "Close"); ToolTip.SetTip(close, "Close · Esc");
        close.Click += (_, _) => window.Close();
        Grid.SetColumn(close, 1); header.Children.Add(close);
        header.PointerPressed += (_, e) =>
        {
            if (e.Handled || !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
            window.BeginMoveDrag(e); e.Handled = true;
        };
        window.Classes.Add("dialog");
        return (window, body, buttons);
    }

    public static async Task<string?> PromptAsync(Window owner, string title, string message, string initial = "", bool allowEmpty = false)
    {
        var (window, body, buttons) = Create(owner, title, message);
        var input = new TextBox { Text = initial };
        input.Classes.Add("path-field");
        body.Children.Add(input);
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        ok.Classes.Add("primary");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => { if (allowEmpty || !string.IsNullOrWhiteSpace(input.Text)) window.Close(input.Text ?? ""); };
        cancel.Click += (_, _) => window.Close(null);
        buttons.Children.Add(ok); buttons.Children.Add(cancel); body.Children.Add(buttons);
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; window.Close(null); } };
        window.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        return await window.ShowDialog<string?>(owner);
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string accept)
    {
        var (window, body, buttons) = Create(owner, title, message);
        var ok = new Button { Content = accept, IsDefault = true, MinWidth = 90 };
        ok.Classes.Add(accept == "Delete permanently" ? "danger" : "primary");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) => window.Close(true);
        cancel.Click += (_, _) => window.Close(false);
        buttons.Children.Add(ok); buttons.Children.Add(cancel); body.Children.Add(buttons);
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; window.Close(false); } };
        window.Opened += (_, _) => ok.Focus();
        return await window.ShowDialog<bool>(owner);
    }

    public static async Task<ConflictResolution> ConflictAsync(Window owner, FileConflict conflict)
    {
        var bothDirectories = conflict.Source.IsDirectory && conflict.Destination.IsDirectory;
        var (window, body, buttons) = Create(owner, "Destination exists",
            $"Destination:\n{conflict.Destination.FullPath}\n\n" +
            (bothDirectories ? "Merge these folders? Conflicting files inside will be asked about separately." : "Choose what to do with the incoming entry."));
        var result = new ConflictResolution(ConflictAction.Cancel);
        foreach (var (label, action) in new[] { (bothDirectories ? "Merge" : "Overwrite", ConflictAction.Overwrite),
                     ("Skip", ConflictAction.Skip), ("Rename", ConflictAction.Rename), ("Cancel", ConflictAction.Cancel) })
        {
            var button = new Button { Content = label, IsCancel = action == ConflictAction.Cancel };
            button.IsEnabled = action != ConflictAction.Overwrite || conflict.Source.IsDirectory == conflict.Destination.IsDirectory;
            button.Click += (_, _) => { result = new(action); window.Close(); };
            buttons.Children.Add(button);
        }
        body.Children.Add(buttons);
        var primary = buttons.Children.OfType<Button>().First(b => b.IsEnabled);
        primary.IsDefault = true;
        primary.Classes.Add("primary");
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; window.Close(); } };
        window.Opened += (_, _) => primary.Focus();
        await window.ShowDialog(owner);
        if (result.Action == ConflictAction.Rename)
        {
            var name = await PromptAsync(owner, "Rename incoming entry", "New name:", conflict.Source.Name);
            return name is null ? new(ConflictAction.Cancel) : new(ConflictAction.Rename, name);
        }
        return result;
    }
}
