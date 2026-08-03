namespace TypeWhisper.PluginSDK;

/// <summary>Describes whether a transcription role/model accepts each language-selection state.</summary>
// ReSharper disable once UnusedType.Global -- public plugin-SDK surface
public interface ITranscriptionLanguageSelectionCapabilities
{
    /// <summary>Whether the currently selected model can detect the spoken language.</summary>
    LanguageSelectionSupport AutomaticDetectionSupport { get; }

    /// <summary>Whether the currently selected model accepts an explicit BCP-47 language tag.</summary>
    LanguageSelectionSupport ExplicitSelectionSupport { get; }
}

/// <summary>Support advertised for one transcription-language selection state.</summary>
// ReSharper disable once UnusedType.Global -- public plugin-SDK surface
public enum LanguageSelectionSupport
{
    /// <summary>The role does not know or did not advertise support.</summary>
    Unknown,

    /// <summary>The role/model accepts this selection state.</summary>
    Supported,

    /// <summary>The role/model rejects this selection state.</summary>
    Unsupported,
}
