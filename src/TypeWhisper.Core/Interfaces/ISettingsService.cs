using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
///     Loads, persists, and broadcasts changes to the application settings.
/// </summary>
public interface ISettingsService
{
    /// <summary>The current in-memory settings (as last loaded or saved).</summary>
    AppSettings Current { get; }

    /// <summary>Reads settings from disk into <see cref="Current" /> and returns them.</summary>
    AppSettings Load();

    /// <summary>Persists <paramref name="settings" />, updates <see cref="Current" />, and raises <see cref="SettingsChanged" />.</summary>
    void Save(AppSettings settings);

    event Action<AppSettings>? SettingsChanged;
}
