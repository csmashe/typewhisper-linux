namespace TypeWhisper.Core.Models;

/// <summary>Model identifiers of the local sherpa-onnx plugin.</summary>
public static class ParakeetModels
{
    private const string TdtModelId = "parakeet-tdt-0.6b";
    private const string UltraModelId = "parakeet-ultra-0.6b";

    /// <summary>
    ///     Whether the model is a Parakeet TDT 0.6B v3 variant. They share one tokenizer, so token
    ///     timings and formatting behave alike.
    /// </summary>
    public static bool IsParakeetTdt(string? modelId) =>
        string.Equals(modelId, TdtModelId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(modelId, UltraModelId, StringComparison.OrdinalIgnoreCase);
}
