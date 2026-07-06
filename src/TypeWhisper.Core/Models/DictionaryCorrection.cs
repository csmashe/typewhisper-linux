namespace TypeWhisper.Core.Models;

/// <summary>
///     A resolved find-and-replace rule applied to transcribed text;
///     <paramref name="CaseSensitive" /> toggles whether <paramref name="Original" />
///     must match casing. Flattened from a <see cref="DictionaryEntry" /> for use
///     by the correction pass.
/// </summary>
public sealed record DictionaryCorrection(string Original, string Replacement, bool CaseSensitive);