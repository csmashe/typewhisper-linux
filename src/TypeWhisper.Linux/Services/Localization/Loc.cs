using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace TypeWhisper.Linux.Services.Localization;

/// <summary>
///     A selectable interface-language option for the settings dropdown.
///     <paramref name="Code" /> is null for "Auto (System)".
/// </summary>
public sealed record UiLanguageOption(string? Code, string DisplayName);

/// <summary>
///     Singleton localization service for the UI. Loads JSON translation files
///     from Resources/Localization/{lang}.json. Fallback chain:
///     selected language -> "en" -> the key itself.
///     Raises PropertyChanged on language change so all bindings refresh,
///     enabling live switching without an app restart.
///     Ported from the upstream Windows app (Loc.cs), adapted for Linux/Avalonia.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private const string FallbackLanguage = "en";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, Dictionary<string, string>> _strings = [];
    private string _currentLanguage = FallbackLanguage;

    private Loc() { }

    public static Loc Instance { get; } = new();

    /// <summary>
    ///     The real OS UI language (two-letter code), captured once at startup
    ///     before any override, so "Auto (System)" can restore the genuine
    ///     system locale. Defaults to "en" until <see cref="App" /> sets it.
    /// </summary>
    public static string SystemLanguage { get; set; } = FallbackLanguage;

    /// <summary>Indexer used by bindings/converters: Loc.Instance["Key"].</summary>
    public string this[string key] => GetString(key);

    public string CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            if (_currentLanguage == value)
            {
                return;
            }

            _currentLanguage = value;

            // Drive both UI-culture (resource lookup) and culture (date/number
            // formatting), on this thread and process-wide, so background work
            // and later-created objects follow the chosen language too.
            try
            {
                var culture = new CultureInfo(value);
                CultureInfo.CurrentUICulture = culture;
                CultureInfo.CurrentCulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
                CultureInfo.DefaultThreadCurrentCulture = culture;
            }
            catch (CultureNotFoundException)
            {
                // Non-standard code (shouldn't happen for our shipped set) —
                // resource lookup still works; formatting stays as-is.
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
            // Empty name => "all properties changed": refreshes indexer bindings too.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<string> AvailableLanguages { get; private set; } = [];

    public IReadOnlyList<UiLanguageOption> AvailableUiLanguages { get; private set; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    /// <summary>
    ///     Loads every Resources/Localization/*.json file found beside the app.
    ///     Call once at startup before the first window is built.
    /// </summary>
    public void Initialize(string? localizationDirOverride = null)
    {
        var localizationDir =
            localizationDirOverride
            ?? Path.Join(AppContext.BaseDirectory, "Resources", "Localization");
        var available = new List<string>();

        if (Directory.Exists(localizationDir))
        {
            foreach (var file in Directory.EnumerateFiles(localizationDir, "*.json"))
            {
                var lang = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrEmpty(lang))
                {
                    continue;
                }

                try
                {
                    var json = File.ReadAllText(file);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json, s_jsonOptions);
                    if (dict is not null)
                    {
                        _strings[lang] = dict;
                        available.Add(lang);
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    Debug.WriteLine($"[Loc] Failed to load {file}: {ex.Message}");
                }
            }
        }

        AvailableLanguages = available;
        AvailableUiLanguages = BuildUiLanguageOptions(available);
    }

    public bool HasLanguage(string langCode) => _strings.ContainsKey(langCode);

    /// <summary>
    ///     Resolves a persisted setting value to an effective language code:
    ///     null/empty => system language if shipped, else English.
    /// </summary>
    public string ResolveLanguage(string? settingValue)
    {
        if (!string.IsNullOrEmpty(settingValue))
        {
            return settingValue;
        }

        return HasLanguage(SystemLanguage) ? SystemLanguage : FallbackLanguage;
    }

    public string GetString(string key)
    {
        if (_strings.TryGetValue(_currentLanguage, out var currentDict)
            && currentDict.TryGetValue(key, out var value))
        {
            return value;
        }

        if (_currentLanguage != FallbackLanguage
            && _strings.TryGetValue(FallbackLanguage, out var fallbackDict)
            && fallbackDict.TryGetValue(key, out var fallbackValue))
        {
            return fallbackValue;
        }

        return key;
    }

    public string GetString(string key, params object?[] args)
    {
        var template = GetString(key);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private static IReadOnlyList<UiLanguageOption> BuildUiLanguageOptions(List<string> codes)
    {
        var displayNames = new Dictionary<string, string>
        {
            ["en"] = "English",
            ["de"] = "Deutsch",
            ["es"] = "Español",
            ["fr"] = "Français",
            ["pt"] = "Português",
            ["it"] = "Italiano",
            ["nl"] = "Nederlands",
            ["pl"] = "Polski",
            ["ru"] = "Русский",
            ["ja"] = "日本語",
            ["zh"] = "中文",
            ["ko"] = "한국어"
        };

        var options = new List<UiLanguageOption> { new(null, "Auto (System)") };
        foreach (var code in codes.OrderBy(c => c, StringComparer.Ordinal))
        {
            var display = displayNames.TryGetValue(code, out var name) ? name : code.ToUpperInvariant();
            options.Add(new UiLanguageOption(code, display));
        }

        return options;
    }
}
