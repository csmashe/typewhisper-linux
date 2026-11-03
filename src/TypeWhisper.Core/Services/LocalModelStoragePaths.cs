using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Resolves storage paths for large local model assets.
/// </summary>
public static class LocalModelStoragePaths
{
    /// <summary>Name of the plugin-asset subfolder created under a custom model-storage root.</summary>
    public const string PluginDataFolderName = "PluginData";

    /// <summary>
    /// Resolves the active local model storage path.
    /// </summary>
    public static string ResolveModelStoragePath(
        AppSettings settings,
        string? defaultModelStoragePath = null
    ) =>
        AppSettings.NormalizeLocalModelStoragePath(settings.LocalModelStoragePath) is { } customPath
            ? Path.GetFullPath(customPath)
            : ResolveDefaultRoot(
                defaultModelStoragePath,
                TypeWhisperEnvironment.ModelsPath,
                nameof(defaultModelStoragePath));

    /// <summary>
    /// Resolves the active plugin asset directory for large model and runtime files.
    /// </summary>
    public static string ResolvePluginAssetDirectory(
        AppSettings? settings,
        string? pluginId,
        string? defaultPluginDataPath = null
    )
    {
        // Reject (rather than silently strip) path separators: stripping would map
        // distinct IDs like "com/test/id" and "id" onto the same directory and risk
        // cross-plugin asset corruption. Check both '/' and '\' regardless of platform.
        var safePluginId = pluginId?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(safePluginId)
            || safePluginId is "." or ".."
            || safePluginId.Contains('/')
            || safePluginId.Contains('\\'))
        {
            throw new ArgumentException(
                "Plugin ID must not be empty or contain path separators.", nameof(pluginId));
        }

        if (settings is null
            || AppSettings.NormalizeLocalModelStoragePath(settings.LocalModelStoragePath) is null)
        {
            return Path.Join(
                ResolveDefaultRoot(
                    defaultPluginDataPath,
                    TypeWhisperEnvironment.PluginDataPath,
                    nameof(defaultPluginDataPath)),
                safePluginId
            );
        }

        return Path.Join(
            ResolveModelStoragePath(settings),
            PluginDataFolderName,
            safePluginId);
    }

    // Keep every result absolute; a blank or relative root would otherwise resolve
    // assets against the process working directory.
    private static string ResolveDefaultRoot(string? injected, string fallback, string paramName)
    {
        if (injected is null)
        {
            return fallback;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(injected, paramName);
        return Path.GetFullPath(injected);
    }
}
