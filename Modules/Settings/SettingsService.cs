using System.IO;
using System.Text.Json;
using PulseGuard.Modules.Settings.Models;

namespace PulseGuard.Modules.Settings;

/// <summary>
/// Loads, persists, and broadcasts live updates for <see cref="AppSettings"/> in %AppData%\PulseGuard\settings.json.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _syncLock = new();
    private readonly string _settingsFilePath;

    public AppSettings Current { get; private set; }

    public string SettingsFilePath => _settingsFilePath;

    public event Action<AppSettings>? SettingsChanged;

    public SettingsService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appDir = Path.Combine(appData, "PulseGuard");
        _settingsFilePath = Path.Combine(appDir, "settings.json");

        Current = LoadFromDisk();
    }

    public void SaveSettings()
    {
        lock (_syncLock)
        {
            Current.Normalize();
            SaveToDiskLocked(Current);
        }
    }

    public void Update(Action<AppSettings> mutate)
    {
        AppSettings snapshot;
        lock (_syncLock)
        {
            mutate(Current);
            Current.Normalize();
            SaveToDiskLocked(Current);
            snapshot = Current.Clone();
        }

        SettingsChanged?.Invoke(snapshot);
    }

    public void ResetToDefaults()
    {
        AppSettings snapshot;
        lock (_syncLock)
        {
            Current = AppSettings.CreateDefaults();
            SaveToDiskLocked(Current);
            snapshot = Current.Clone();
        }

        SettingsChanged?.Invoke(snapshot);
    }

    private AppSettings LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                var defaults = AppSettings.CreateDefaults();
                SaveToDiskLocked(defaults);
                return defaults;
            }

            string json = File.ReadAllText(_settingsFilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                         ?? AppSettings.CreateDefaults();
            loaded.Normalize();
            return loaded;
        }
        catch
        {
            return AppSettings.CreateDefaults();
        }
    }

    private void SaveToDiskLocked(AppSettings settings)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(settings, JsonOptions);
            string tempFilePath = _settingsFilePath + ".tmp";
            File.WriteAllText(tempFilePath, json);
            File.Move(tempFilePath, _settingsFilePath, overwrite: true);
        }
        catch
        {
            // Ignore disk write errors if %AppData% is temporarily unavailable.
        }
    }
}

