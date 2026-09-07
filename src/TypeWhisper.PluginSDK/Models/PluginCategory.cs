using System.Text.Json.Serialization;

namespace TypeWhisper.PluginSDK.Models;

/// <summary>
///     A capability category exposed by a plugin. Plugins may declare more than one.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PluginCategory>))]
public enum PluginCategory
{
    Transcription,
    Llm,
    Tts,
    PostProcessing,
    Action,
    Memory,
    Integration,
    Utility,

    /// <summary>
    ///     Host fallback for an external plugin whose capability cannot be determined.
    ///     Bundled manifests must not declare this value.
    /// </summary>
    Unknown,
}
