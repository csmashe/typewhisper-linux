namespace TypeWhisper.PluginSDK;

/// <summary>Thrown before transcription when a role/model rejects a language selection.</summary>
// ReSharper disable once UnusedType.Global -- public plugin-SDK surface
public sealed class LanguageSelectionNotSupportedException : NotSupportedException
{
    /// <summary>Creates an exception for the rejected role/model selection.</summary>
    public LanguageSelectionNotSupportedException(
        string providerId,
        string? modelId,
        LanguageSelection selection
    )
        : base(CreateMessage(providerId, modelId, selection))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(selection);
        ProviderId = providerId;
        ModelId = modelId;
        Selection = selection;
    }

    /// <summary>The provider whose current model rejected the selection.</summary>
    public string ProviderId { get; }

    /// <summary>The selected provider model, if known.</summary>
    public string? ModelId { get; }

    /// <summary>The rejected typed selection.</summary>
    public LanguageSelection Selection { get; }

    private static string CreateMessage(
        string providerId,
        string? modelId,
        LanguageSelection selection
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(selection);
        var target = string.IsNullOrWhiteSpace(modelId)
            ? $"Provider '{providerId}'"
            : $"Provider '{providerId}' model '{modelId}'";
        return selection.IsAutomatic
            ? $"{target} does not support automatic language detection. Choose an explicit language."
            : $"{target} does not support explicit language selection. Choose automatic language detection.";
    }
}
