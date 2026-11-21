using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using PulseGuard.Modules.Optimization.Models;

namespace PulseGuard.Modules.Optimization;

/// <summary>
/// Persists original Windows Registry values to %AppData%\PulseGuard\registry_backup.json
/// before any modification and never overwrites an already-saved original entry.
/// </summary>
public sealed class RegistryBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _syncLock = new();
    private readonly string _backupFilePath;
    private RegistryBackupFile _backup;

    public string BackupFilePath => _backupFilePath;

    public bool HasBackup
    {
        get
        {
            lock (_syncLock)
            {
                return _backup.Entries.Count > 0;
            }
        }
    }

    public int BackupCount
    {
        get
        {
            lock (_syncLock)
            {
                return _backup.Entries.Count;
            }
        }
    }

    public event Action? BackupChanged;

    public RegistryBackupService()
    {
        string appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PulseGuard");
        Directory.CreateDirectory(appDataDir);
        _backupFilePath = Path.Combine(appDataDir, "registry_backup.json");
        _backup = LoadBackup();
    }

    /// <summary>
    /// Backs up the original registry value (if not already backed up) and applies the new value.
    /// </summary>
    public bool BackupAndSetValue(
        RegistryHive hive,
        string subKey,
        string valueName,
        object newValue,
        RegistryValueKind valueKind)
    {
        bool changedBackup = false;

        lock (_syncLock)
        {
            try
            {
                string hiveName = HiveToString(hive);
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

                bool alreadyBackedUp = _backup.Entries.Any(e =>
                    string.Equals(e.Hive, hiveName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.SubKey, subKey, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.ValueName, valueName, StringComparison.OrdinalIgnoreCase));

                RegistryBackupEntry? pendingEntry = null;
                if (!alreadyBackedUp)
                {
                    using var readKey = baseKey.OpenSubKey(subKey, writable: false);
                    object? currentVal = readKey?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    bool existed = currentVal is not null;
                    RegistryValueKind originalKind = existed && readKey is not null
                        ? readKey.GetValueKind(valueName)
                        : valueKind;

                    pendingEntry = new RegistryBackupEntry
                    {
                        Hive = hiveName,
                        SubKey = subKey,
                        ValueName = valueName,
                        ValueExisted = existed,
                        ValueKind = originalKind.ToString(),
                        OriginalValue = SerializeValue(currentVal, originalKind)
                    };
                }

                using var writeKey = baseKey.CreateSubKey(subKey, writable: true);
                if (writeKey is null)
                {
                    return false;
                }

                writeKey.SetValue(valueName, newValue, valueKind);

                if (pendingEntry is not null)
                {
                    _backup.Entries.Add(pendingEntry);
                    _backup.LastUpdatedUtc = DateTime.UtcNow;
                    SaveBackupLocked();
                    changedBackup = true;
                }
            }
            catch
            {
                return false;
            }
        }

        if (changedBackup)
        {
            BackupChanged?.Invoke();
        }

        return true;
    }

    public string? ReadCurrentStringValue(RegistryHive hive, string subKey, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var readKey = baseKey.OpenSubKey(subKey, writable: false);
            return readKey?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Restores all backed-up registry entries to their original state.
    /// </summary>
    public (int RestoredCount, int FailedCount) RollbackAll()
    {
        int restored = 0;
        int failed = 0;
        bool changedBackup = false;

        lock (_syncLock)
        {
            if (_backup.Entries.Count == 0)
            {
                return (0, 0);
            }

            var remaining = new List<RegistryBackupEntry>();

            for (int i = _backup.Entries.Count - 1; i >= 0; i--)
            {
                var entry = _backup.Entries[i];
                if (TryRestoreEntry(entry))
                {
                    restored++;
                }
                else
                {
                    failed++;
                    remaining.Add(entry);
                }
            }

            remaining.Reverse();
            _backup.Entries = remaining;
            _backup.LastUpdatedUtc = DateTime.UtcNow;
            SaveBackupLocked();
            changedBackup = true;
        }

        if (changedBackup)
        {
            BackupChanged?.Invoke();
        }

        return (restored, failed);
    }

    private static bool TryRestoreEntry(RegistryBackupEntry entry)
    {
        try
        {
            if (!TryParseHive(entry.Hive, out var hive))
            {
                return false;
            }

            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

            if (!entry.ValueExisted)
            {
                using var writeKey = baseKey.OpenSubKey(entry.SubKey, writable: true);
                writeKey?.DeleteValue(entry.ValueName, throwOnMissingValue: false);
                return true;
            }

            if (!Enum.TryParse<RegistryValueKind>(entry.ValueKind, out var kind))
            {
                kind = RegistryValueKind.String;
            }

            object? restoredValue = DeserializeValue(entry.OriginalValue, kind);
            if (restoredValue is null)
            {
                return false;
            }

            using var key = baseKey.CreateSubKey(entry.SubKey, writable: true);
            if (key is null)
            {
                return false;
            }

            key.SetValue(entry.ValueName, restoredValue, kind);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private RegistryBackupFile LoadBackup()
    {
        try
        {
            if (!File.Exists(_backupFilePath))
            {
                return new RegistryBackupFile();
            }

            var json = File.ReadAllText(_backupFilePath);
            return JsonSerializer.Deserialize<RegistryBackupFile>(json, JsonOptions) ?? new RegistryBackupFile();
        }
        catch
        {
            return new RegistryBackupFile();
        }
    }

    private void SaveBackupLocked()
    {
        try
        {
            string tmpPath = _backupFilePath + ".tmp";
            string json = JsonSerializer.Serialize(_backup, JsonOptions);
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, _backupFilePath, overwrite: true);
        }
        catch
        {
            // Ignore atomic save errors.
        }
    }

    private static string HiveToString(RegistryHive hive) => hive switch
    {
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.LocalMachine => "HKLM",
        _ => hive.ToString()
    };

    private static bool TryParseHive(string hive, out RegistryHive result)
    {
        if (string.Equals(hive, "HKCU", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hive, nameof(RegistryHive.CurrentUser), StringComparison.OrdinalIgnoreCase))
        {
            result = RegistryHive.CurrentUser;
            return true;
        }

        if (string.Equals(hive, "HKLM", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hive, nameof(RegistryHive.LocalMachine), StringComparison.OrdinalIgnoreCase))
        {
            result = RegistryHive.LocalMachine;
            return true;
        }

        result = default;
        return false;
    }

    private static string? SerializeValue(object? value, RegistryValueKind kind)
    {
        if (value is null)
        {
            return null;
        }

        return kind switch
        {
            RegistryValueKind.DWord => Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            RegistryValueKind.QWord => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            RegistryValueKind.Binary when value is byte[] bytes => Convert.ToBase64String(bytes),
            RegistryValueKind.MultiString when value is string[] arr => JsonSerializer.Serialize(arr),
            _ => value.ToString()
        };
    }

    private static object? DeserializeValue(string? serialized, RegistryValueKind kind)
    {
        if (serialized is null)
        {
            return null;
        }

        return kind switch
        {
            RegistryValueKind.DWord => int.TryParse(serialized, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) ? d : null,
            RegistryValueKind.QWord => long.TryParse(serialized, NumberStyles.Integer, CultureInfo.InvariantCulture, out long q) ? q : null,
            RegistryValueKind.Binary => Convert.FromBase64String(serialized),
            RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(serialized) ?? [],
            _ => serialized
        };
    }
}

