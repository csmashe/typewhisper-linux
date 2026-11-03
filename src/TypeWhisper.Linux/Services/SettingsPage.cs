using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     A settings destination named on the command line with <c>--page</c>: one of the
///     stable <see cref="Names" />, or <c>plugin:&lt;id&gt;</c> for one plugin's settings.
/// </summary>
internal sealed record SettingsPage
{
    private const string PluginPrefix = "plugin:";
    private const int MaxPluginIdLength = 128;

    // Names are part of the command-line contract: keep them English and stable even when
    // a section's label or position changes.
    private static readonly (string Name, Type Section)[] s_pages =
    [
        ("dashboard", typeof(DashboardSectionViewModel)),
        ("dictation", typeof(DictationSectionViewModel)),
        ("shortcuts", typeof(ShortcutsSectionViewModel)),
        ("text-insertion", typeof(TextInsertionSectionViewModel)),
        ("file-transcription", typeof(FileTranscriptionSectionViewModel)),
        ("recorder", typeof(RecorderSectionViewModel)),
        ("history", typeof(HistorySectionViewModel)),
        ("dictionary", typeof(DictionarySectionViewModel)),
        ("snippets", typeof(SnippetsSectionViewModel)),
        ("profiles", typeof(ProfilesSectionViewModel)),
        ("prompts", typeof(PromptsSectionViewModel)),
        ("plugins", typeof(PluginsSectionViewModel)),
        ("general", typeof(GeneralSectionViewModel)),
        ("appearance", typeof(AppearanceSectionViewModel)),
        ("advanced", typeof(AdvancedSectionViewModel)),
        ("about", typeof(AboutSectionViewModel)),
    ];

    private SettingsPage(string name, Type section, string? pluginId)
    {
        Name = name;
        Section = section;
        PluginId = pluginId;
    }

    public static IEnumerable<string> Names => s_pages.Select(page => page.Name);

    /// <summary>The canonical form, e.g. <c>dictation</c> or <c>plugin:com.typewhisper.openai</c>.</summary>
    public string Name { get; }

    /// <summary>The section view model type the main window shows.</summary>
    public Type Section { get; }

    /// <summary>Set for <c>plugin:&lt;id&gt;</c>; the plugin may not be installed.</summary>
    public string? PluginId { get; }

    /// <summary>
    ///     Page names match case-insensitively; plugin IDs are kept as given because
    ///     plugin IDs compare ordinally.
    /// </summary>
    public static SettingsPage? TryParse(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        foreach (var (name, section) in s_pages)
        {
            if (string.Equals(value, name, StringComparison.OrdinalIgnoreCase))
            {
                return new SettingsPage(name, section, null);
            }
        }

        if (!value.StartsWith(PluginPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var id = value[PluginPrefix.Length..];
        return IsValidPluginId(id)
            ? new SettingsPage(PluginPrefix + id, typeof(PluginsSectionViewModel), id)
            : null;
    }

    private static bool IsValidPluginId(string id) =>
        id.Length is > 0 and <= MaxPluginIdLength
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}
