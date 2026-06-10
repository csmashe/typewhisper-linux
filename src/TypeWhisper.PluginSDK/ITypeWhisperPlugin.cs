namespace TypeWhisper.PluginSDK;

/// <summary>
///     Base interface for all TypeWhisper plugins. The host calls <c>ActivateAsync</c> once
///     after construction and <c>DeactivateAsync</c> before <c>Dispose</c>. Neither method
///     may block — long-running work must run on a background task.
/// </summary>
public interface ITypeWhisperPlugin : IDisposable
{
    /// <summary>Unique identifier for the plugin (e.g. "com.example.my-plugin").</summary>
    string PluginId { get; }

    /// <summary>Human-readable display name.</summary>
    string PluginName { get; }

    /// <summary>Semantic version string (e.g. "1.0.0").</summary>
    string PluginVersion { get; }

    Task ActivateAsync(IPluginHostServices host);

    Task DeactivateAsync();
}