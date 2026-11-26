using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Automation;
using DockCommander.Core.Operations;
using DockCommander.Core.Files;
using DockCommander.Core.Connections;

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

    public static async Task<string?> PromptAsync(Window owner, string title, string message, string initial = "", bool allowEmpty = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        using var cancellation = cancellationToken.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => window.Close()));
        return await window.ShowDialog<string?>(owner);
    }

    public static async Task<SftpCredentials?> SftpLoginAsync(Window owner, ConnectionProfile profile, SftpCredentials? previous = null)
    {
        var key = profile.Authentication == AuthenticationKinds.PrivateKey;
        var remembered = key
            ? "A remembered private key stays encrypted for this user account."
            : "A remembered password stays encrypted for this user account.";
        var (window, body, buttons) = Create(owner, "Connect",
            previous is null
                ? $"Sign in to {profile.Name}. {remembered}"
                : $"Could not connect to {profile.Name}. Enter the sign-in details again. {remembered}");
        TextBox? password = null;
        TextBox? keyPath = null;
        TextBox? passphrase = null;
        if (key)
        {
            keyPath = SecretField("SftpKeyPath", previous?.KeyPath ?? "", password: false);
            passphrase = SecretField("SftpPassphrase", previous?.Passphrase ?? "", password: true);
            Add("Private key file", keyPath);
            Add("Key passphrase", passphrase);
        }
        else
        {
            password = SecretField("SftpPassword", previous?.Password ?? "", password: true);
            Add("Password", password);
        }
        var remember = new CheckBox
        {
            Name = "RememberSecret",
            IsChecked = previous?.Remember ?? true,
            Content = key ? "Remember private key" : "Remember password",
            Margin = new Thickness(0, 4, 0, 0)
        };
        body.Children.Add(remember);
        var connect = new Button { Content = "Connect", IsDefault = true, MinWidth = 90 };
        connect.Classes.Add("primary");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        connect.Click += (_, _) => window.Close(new SftpCredentials(
            profile.Authentication, password?.Text ?? "", keyPath?.Text ?? "", passphrase?.Text ?? "", remember.IsChecked == true));
        cancel.Click += (_, _) => window.Close(null);
        buttons.Children.Add(connect); buttons.Children.Add(cancel); body.Children.Add(buttons);
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; window.Close(null); } };
        window.Opened += (_, _) =>
        {
            var field = key ? keyPath! : password!;
            field.Focus();
            field.SelectAll();
        };
        return await window.ShowDialog<SftpCredentials?>(owner);

        void Add(string caption, Control control)
        {
            var label = new TextBlock { Text = caption, FontSize = 12 };
            label.Classes.Add("muted");
            body.Children.Add(label);
            body.Children.Add(control);
        }
    }

    private static TextBox SecretField(string name, string value, bool password)
    {
        var box = new TextBox { Name = name, Text = value };
        if (password) box.PasswordChar = '●';
        box.Classes.Add("path-field");
        return box;
    }

    public static async Task<ConnectionEditResult?> EditConnectionAsync(Window owner, ConnectionProfile? existing)
    {
        var (window, body, buttons) = Create(owner, existing is null ? "Add connection" : "Edit connection",
            "The password is asked on Connect unless you choose to remember it. A remembered password or private key is encrypted for this user account.");
        var name = Field("Name", existing?.Name ?? "");
        var protocol = new ComboBox { ItemsSource = ConnectionProtocols.All, SelectedItem = existing?.Protocol ?? ConnectionProtocols.Sftp };
        var host = Field("Host", existing?.Host ?? "");
        var port = Field("Port", existing?.Port?.ToString() ?? ConnectionProtocols.DefaultPort(existing?.Protocol ?? ConnectionProtocols.Sftp)?.ToString() ?? "");
        var username = Field("Username", existing?.Username ?? "");
        var initialPath = Field("Initial path", existing?.InitialPath ?? "");
        initialPath.PlaceholderText = "Remote folder, optional";
        var authentication = new ComboBox { ItemsSource = AuthenticationKinds.All, SelectedItem = existing?.Authentication ?? AuthenticationKinds.Password };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        error.Classes.Add("muted");
        string? previousDefault = port.Text;
        protocol.SelectionChanged += (_, _) =>
        {
            var next = ConnectionProtocols.DefaultPort(protocol.SelectedItem as string ?? "")?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(port.Text) || port.Text == previousDefault) port.Text = next;
            previousDefault = next;
        };
        foreach (var (caption, control) in new (string, Control)[]
        {
            ("Name", name), ("Protocol", protocol), ("Host", host), ("Port", port),
            ("Username", username), ("Initial path", initialPath), ("Authentication", authentication)
        })
        {
            body.Children.Add(new TextBlock { Text = caption, FontSize = 12 });
            ((TextBlock)body.Children[^1]).Classes.Add("muted");
            body.Children.Add(control);
        }
        body.Children.Add(error);
        var save = new Button { Content = "Save", IsDefault = true, MinWidth = 90 };
        save.Classes.Add("primary");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        cancel.Click += (_, _) => window.Close(null);
        save.Click += (_, _) =>
        {
            int? portValue = null;
            if (!string.IsNullOrWhiteSpace(port.Text))
            {
                if (!int.TryParse(port.Text, out var parsed)) { error.Text = "Port must be between 1 and 65535."; return; }
                portValue = parsed;
            }
            var protocolName = protocol.SelectedItem as string ?? "";
            var hostName = (host.Text ?? "").Trim();
            var sameServer = existing is not null
                && string.Equals(existing.Protocol, protocolName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.Host, hostName, StringComparison.OrdinalIgnoreCase)
                && (existing.Port ?? ConnectionProtocols.DefaultPort(existing.Protocol)) == (portValue ?? ConnectionProtocols.DefaultPort(protocolName));
            var profile = new ConnectionProfile(
                existing?.Id ?? Guid.NewGuid().ToString("n"),
                name.Text ?? "",
                protocolName,
                host.Text ?? "",
                portValue,
                username.Text ?? "",
                initialPath.Text ?? "",
                authentication.SelectedItem as string ?? "",
                sameServer ? existing!.HostFingerprint : null).Normalized();
            if (ConnectionProfile.Validate(profile) is { } message) { error.Text = message; return; }
            window.Close(new ConnectionEditResult(profile, false));
        };
        if (existing is not null)
        {
            var remove = new Button { Content = "Remove", MinWidth = 90 };
            remove.Classes.Add("danger");
            remove.Click += async (_, _) =>
            {
                if (await ConfirmAsync(window, "Remove connection", $"Remove {existing.Name}?", "Remove"))
                    window.Close(new ConnectionEditResult(null, true));
            };
            buttons.Children.Add(remove);
        }
        buttons.Children.Add(save); buttons.Children.Add(cancel); body.Children.Add(buttons);
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; window.Close(null); } };
        window.Opened += (_, _) => { name.Focus(); name.SelectAll(); };
        return await window.ShowDialog<ConnectionEditResult?>(owner);
    }

    private static TextBox Field(string name, string value)
    {
        var box = new TextBox { Name = name.Replace(" ", ""), Text = value };
        box.Classes.Add("path-field");
        return box;
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

    public static async Task<ConflictResolution> ConflictAsync(Window owner, FileConflict conflict, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        using var cancellation = cancellationToken.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => window.Close()));
        await window.ShowDialog(owner);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Action == ConflictAction.Rename)
        {
            var name = await PromptAsync(owner, "Rename incoming entry", "New name:", conflict.Source.Name, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return name is null ? new(ConflictAction.Cancel) : new(ConflictAction.Rename, name);
        }
        return result;
    }
}

internal sealed record ConnectionEditResult(ConnectionProfile? Profile, bool Removed);
