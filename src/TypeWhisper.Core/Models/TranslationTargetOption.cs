namespace TypeWhisper.Core.Models;

/// <summary>
///     Display item for translation target language ComboBoxes.
/// </summary>
public sealed record TranslationTargetOption(string? Code, string DisplayName)
{
    // A collapsed ComboBox announces its selected item through ToString().
    public override string ToString() => DisplayName;
}
