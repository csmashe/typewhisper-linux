using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
///     Stores text snippets and expands their triggers (with placeholder support)
///     inside transcribed text.
/// </summary>
public interface ISnippetService
{
    IReadOnlyList<Snippet> Snippets { get; }

    /// <summary>All distinct tags used across the snippets.</summary>
    IReadOnlyList<string> AllTags { get; }

    void AddSnippet(Snippet snippet);
    void UpdateSnippet(Snippet snippet);
    void DeleteSnippet(string id);

    /// <summary>
    ///     Expands snippet triggers found in <paramref name="text" />.
    ///     <paramref name="clipboardProvider" /> supplies the value for clipboard placeholders;
    ///     <paramref name="profileId" /> scopes matching to a profile when set.
    /// </summary>
    string ApplySnippets(
        string text,
        Func<string>? clipboardProvider = null,
        string? profileId = null
    );

    /// <summary>Expands the placeholders in a single replacement string, for live preview in the editor.</summary>
    string PreviewReplacement(string replacement, Func<string>? clipboardProvider = null);

    string ExportToJson();

    /// <summary>Imports snippets from JSON, returning the number added.</summary>
    int ImportFromJson(string json);

    event Action? SnippetsChanged;
}
