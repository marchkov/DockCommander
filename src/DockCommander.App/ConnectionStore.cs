using System.Text.Json;
using DockCommander.Core.Connections;

namespace DockCommander.App;

internal sealed class ConnectionStore(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string Path => path;
    public string? LoadWarning { get; private set; }

    public static string Beside(string settingsPath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(settingsPath)!, "connections.json");

    public static ConnectionStore Beside(SettingsStore settings) => new(Beside(settings.FilePath));

    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync()
    {
        try
        {
            if (!File.Exists(path)) return [];
            var profiles = JsonSerializer.Deserialize<List<ConnectionProfile>>(await File.ReadAllTextAsync(path)) ?? [];
            var accepted = new List<ConnectionProfile>();
            foreach (var profile in profiles)
            {
                if (profile is null) continue;
                var normalized = profile.Normalized();
                if (ConnectionProfile.Validate(normalized) is not null)
                {
                    LoadWarning = "A saved connection was skipped because it is incomplete.";
                    continue;
                }
                accepted.Add(normalized);
            }
            return accepted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadWarning = $"Could not read connections: {ex.Message}";
            return [];
        }
    }

    public async Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles)
    {
        var normalized = profiles.Select(profile => profile.Normalized()).ToArray();
        var invalid = normalized.Select(ConnectionProfile.Validate).FirstOrDefault(error => error is not null);
        if (invalid is not null) throw new InvalidOperationException(invalid);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(normalized, Json));
        File.Move(temporary, path, true);
    }
}
