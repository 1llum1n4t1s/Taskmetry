using System.Text.Json;
using Taskmetry.Models;

namespace Taskmetry.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _settingsPath;

    public SettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Taskmetry",
            "settings.json");
    }

    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? SettingsChanged;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions) ?? new AppSettings();
            }
        }
        catch (JsonException)
        {
            Current = new AppSettings();
        }
        catch (IOException)
        {
            Current = new AppSettings();
        }
        catch (UnauthorizedAccessException)
        {
            Current = new AppSettings();
        }

        Current.Sanitize();
        return Current;
    }

    public void Save(AppSettings settings)
    {
        settings.Sanitize();
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _settingsPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tempPath, _settingsPath, overwrite: true);
        Current = settings.Clone();
        SettingsChanged?.Invoke(this, Current);
    }
}
