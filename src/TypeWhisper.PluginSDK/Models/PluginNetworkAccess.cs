using System.Text.Json.Serialization;

namespace TypeWhisper.PluginSDK.Models;

/// <summary>
///     Describes whether plugin operations can send data beyond the local machine.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PluginNetworkAccess>))]
public enum PluginNetworkAccess
{
    /// <summary>All processing stays on the local machine.</summary>
    Local,

    /// <summary>The plugin sends data to a fixed network service.</summary>
    Network,

    /// <summary>The plugin combines local processing with network service calls.</summary>
    Mixed,

    /// <summary>The destination or executable behavior is selected by the user.</summary>
    UserControlled,
}
