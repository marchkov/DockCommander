using System.Text.Json;

namespace DockCommander.App;

internal sealed record AppSettings(string LeftPath, string RightPath, double Width = 1200, double Height = 760,
    bool LeftActive = true, bool Maximized = false, string Theme = "Light", string EditorExecutable = "");

internal sealed class SettingsStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DockCommander", "settings.json");
    public string? LoadWarning { get; private set; }

    public async Task<AppSettings> LoadAsync()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            if (File.Exists(_path))
            {
                var result = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(_path));
                if (result is not null && !string.IsNullOrWhiteSpace(result.LeftPath) && !string.IsNullOrWhiteSpace(result.RightPath))
                    return result;
                LoadWarning = "Invalid settings; using defaults.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadWarning = $"Could not read settings: {ex.Message}";
        }
        return new(home, home);
    }

    public async Task SaveAsync(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, true);
    }
}
