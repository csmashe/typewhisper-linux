using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.FillerWords;

/// <summary>Holds the user-configured filler word list and persists it through the host.</summary>
public sealed class FillerWordsSettingsStore
{
    private const string WordsKey = "words";

    private readonly IPluginHostServices _host;
    private string _wordsText;

    internal FillerWordsSettingsStore(IPluginHostServices host)
    {
        _host = host;

        var stored = host.GetSetting<string>(WordsKey);
        if (stored is null)
        {
            _wordsText = DefaultWordsText;
            host.SetSetting(WordsKey, _wordsText);
        }
        else
        {
            _wordsText = stored;
        }
    }

    /// <summary>Gets or sets the raw filler word list as entered by the user.</summary>
    public string WordsText
    {
        get => _wordsText;
        set
        {
            if (string.Equals(_wordsText, value, StringComparison.Ordinal))
            {
                return;
            }

            _wordsText = value;
            _host.SetSetting(WordsKey, value);
        }
    }

    /// <summary>Gets every configured filler word parsed from <see cref="WordsText"/>, regardless of language scope.</summary>
    private IReadOnlyList<string> Words => FillerWordFilter.NormalizeWords(_wordsText);

    /// <summary>Gets the filler words that apply to <paramref name="language"/>.</summary>
    public IReadOnlyList<string> WordsFor(string? language) => FillerWordFilter.ParseWordList(_wordsText).WordsFor(language);

    /// <summary>Gets the number of distinct filler words currently configured.</summary>
    public int WordCount => Words.Count;

    /// <summary>Restores the built-in filler word list.</summary>
    public void ResetToDefaults() => WordsText = DefaultWordsText;

    /// <summary>Gets the built-in filler word list as the user sees it.</summary>
    public static string DefaultWordsText => FillerWordFilter.DefaultWordsText;
}
