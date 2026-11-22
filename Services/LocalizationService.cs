using System.Windows;

namespace PulseGuard.Services;

public sealed record LanguageOption(string Code, string DisplayText);

/// <summary>
/// Manages runtime UI localization via WPF ResourceDictionary switching.
/// </summary>
public sealed class LocalizationService
{
    private const string DictionaryUriPrefix = "Resources/Localization/Strings.";

    public static IReadOnlyList<LanguageOption> SupportedLanguages { get; } =
    [
        new("ru", "Русский"),
        new("en", "English"),
        new("uk", "Українська"),
        new("pl", "Polski")
    ];

    private ResourceDictionary? _activeDictionary;

    public string CurrentLanguage { get; private set; } = "ru";

    public event Action<string>? LanguageChanged;

    public static string NormalizeLanguageCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "ru";
        }

        return code.Trim().ToLowerInvariant() switch
        {
            "en" => "en",
            "uk" or "ua" => "uk",
            "pl" => "pl",
            _ => "ru"
        };
    }

    public void ApplyLanguage(string? languageCode)
    {
        string normalized = NormalizeLanguageCode(languageCode);
        var app = Application.Current;
        if (app is null)
        {
            CurrentLanguage = normalized;
            return;
        }

        var merged = app.Resources.MergedDictionaries;
        var uri = new Uri($"{DictionaryUriPrefix}{normalized}.xaml", UriKind.Relative);
        var newDict = new ResourceDictionary { Source = uri };

        if (_activeDictionary is not null)
        {
            merged.Remove(_activeDictionary);
        }

        // Remove any initial Strings.ru.xaml from App.xaml on first switch if not tracked yet
        if (_activeDictionary is null)
        {
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                if (merged[i].Source?.OriginalString.Contains("Resources/Localization/Strings.", StringComparison.OrdinalIgnoreCase) == true)
                {
                    merged.RemoveAt(i);
                }
            }
        }

        merged.Add(newDict);
        _activeDictionary = newDict;
        CurrentLanguage = normalized;
        LanguageChanged?.Invoke(normalized);
    }

    public string Get(string key, string fallback = "")
    {
        if (Application.Current?.TryFindResource(key) is string value)
        {
            return value;
        }

        return fallback;
    }
}

