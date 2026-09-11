using System.Text.Json.Serialization;

namespace TypeWhisper.Core.Models;

/// <summary>
///     Decides whether both an app rule and a URL rule must match a profile.
/// </summary>
[JsonConverter(
    typeof(JsonStringEnumConverter<ProfileContextMatchMode>))]
public enum ProfileContextMatchMode
{
    /// <summary>Both an app rule and a URL rule must match (legacy behaviour).</summary>
    All,
    /// <summary>Either an app rule or a URL rule is enough.</summary>
    Any,
}
