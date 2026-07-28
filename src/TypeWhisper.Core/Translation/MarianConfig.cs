using System.Text.Json;

namespace TypeWhisper.Core.Translation;

/// <summary>
/// Represents marian config data.
/// </summary>
/// <param name="DecoderStartTokenId">Decoder start token id supplied to the member.</param>
/// <param name="EosTokenId">Eos token id supplied to the member.</param>
/// <param name="VocabSize">Vocab size supplied to the member.</param>
/// <param name="MaxLength">Max length supplied to the member.</param>
public sealed record MarianConfig(
    int DecoderStartTokenId,
    int EosTokenId,
    // ReSharper disable once NotAccessedPositionalProperty.Global
    int MaxLength
)
{
    /// <summary>
    /// Loads persisted state from storage.
    /// </summary>
    public static MarianConfig Load(string configJsonPath)
    {
        var json = File.ReadAllText(configJsonPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new MarianConfig(
            root.GetProperty("decoder_start_token_id").GetInt32(),
            root.TryGetProperty("eos_token_id", out var eos) ? eos.GetInt32() : 0,
            root.TryGetProperty("max_length", out var maxLen) ? maxLen.GetInt32() : 512
        );
    }
}