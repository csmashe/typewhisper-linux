using System.Text.Json.Serialization;

namespace TypeWhisper.Core.Models;

/// <summary>Decides when a profile presses Enter after inserting its dictation.</summary>
[JsonConverter(
    typeof(JsonStringEnumConverter<ProfileAutoEnterMode>))]
public enum ProfileAutoEnterMode
{
    /// <summary>Presses Enter when the dictation ends with "press enter", which is removed from the text.</summary>
    SpokenCommand,
    /// <summary>Only inserts the text; a spoken "press enter" stays in it as words.</summary>
    Never,
    /// <summary>Presses Enter after every dictation that inserted text.</summary>
    Always,
}
