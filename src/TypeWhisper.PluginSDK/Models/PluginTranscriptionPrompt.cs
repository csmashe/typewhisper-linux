using System.Text;
using System.Text.Json;

namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// The transcription prompt the host sends to an engine that opts into
/// <see cref="ITranscriptionEngineRole.SupportsStructuredDictionaryTerms" />: free prompt text and
/// dictionary terms kept apart, so a term such as <c>Washington, D.C.</c> reaches the provider as
/// one entry. Engines that do not opt in keep receiving the plain-text prompt.
/// </summary>
public sealed class PluginTranscriptionPrompt
{
    /// <summary>Marks the versioned envelope at the start of a <c>prompt</c> argument.</summary>
    public const string EnvelopePrefix = "TypeWhisper.TranscriptionPrompt/1\n";

    private const string TextProperty = "text";
    private const string DictionaryTermsProperty = "dictionaryTerms";

    private PluginTranscriptionPrompt(string? text, IReadOnlyList<string> dictionaryTerms)
    {
        Text = text;
        DictionaryTerms = dictionaryTerms;
    }

    /// <summary>Free prompt text (for example an API caller's prompt), or null when there is none.</summary>
    public string? Text { get; }

    /// <summary>Normalized dictionary terms in their original order, already clipped to the engine's budget.</summary>
    public IReadOnlyList<string> DictionaryTerms { get; }

    /// <summary>
    /// Encodes prompt text and dictionary terms as an envelope, or returns null when both are empty.
    /// Terms are normalized but not clipped; apply the engine's budget first.
    /// </summary>
    public static string? Encode(string? text, IEnumerable<string>? dictionaryTerms)
    {
        var normalizedText = NormalizeText(text);
        var terms = PluginDictionaryTerms.Normalize(dictionaryTerms);
        if (normalizedText is null && terms.Count == 0)
            return null;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (normalizedText is not null)
                writer.WriteString(TextProperty, normalizedText);
            writer.WriteStartArray(DictionaryTermsProperty);
            foreach (var term in terms)
                writer.WriteStringValue(term);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return EnvelopePrefix + Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Reads a <c>prompt</c> argument. An envelope yields its text and terms; any other value is
    /// plain prompt text with no dictionary terms. A malformed envelope throws
    /// <see cref="FormatException" /> rather than turning into unintended prompt text.
    /// </summary>
    public static PluginTranscriptionPrompt Parse(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return new PluginTranscriptionPrompt(null, []);
        if (!prompt.StartsWith(EnvelopePrefix, StringComparison.Ordinal))
            return new PluginTranscriptionPrompt(prompt.Trim(), []);

        try
        {
            using var document = JsonDocument.Parse(prompt.AsMemory(EnvelopePrefix.Length));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("The transcription prompt envelope must be a JSON object.");

            string? text = null;
            if (root.TryGetProperty(TextProperty, out var textElement)
                && textElement.ValueKind != JsonValueKind.Null)
            {
                text = textElement.ValueKind == JsonValueKind.String
                    ? NormalizeText(textElement.GetString())
                    : throw new FormatException("The transcription prompt text must be a string.");
            }

            if (!root.TryGetProperty(DictionaryTermsProperty, out var termsElement)
                || termsElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("The transcription prompt envelope must contain a dictionary term array.");
            }

            var terms = termsElement.EnumerateArray()
                .Select(term => term.ValueKind == JsonValueKind.String
                    ? term.GetString()!
                    : throw new FormatException("Every dictionary term must be a string."))
                .ToList();

            return new PluginTranscriptionPrompt(text, PluginDictionaryTerms.Normalize(terms));
        }
        catch (JsonException ex)
        {
            throw new FormatException("The transcription prompt envelope is not valid JSON.", ex);
        }
    }

    /// <summary>
    /// The plain-text prompt an engine without structured terms would have received:
    /// the text, then the comma-separated terms on their own line.
    /// </summary>
    public string? ToPlainText()
    {
        var terms = DictionaryTerms.Count == 0 ? null : string.Join(", ", DictionaryTerms);
        return Text is null ? terms
            : terms is null ? Text
            : Text + Environment.NewLine + terms;
    }

    private static string? NormalizeText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
