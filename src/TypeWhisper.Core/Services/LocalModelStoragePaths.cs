using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Resolves storage paths for large local model assets.
/// </summary>
public static class LocalModelStoragePaths
{
    /// <summary>Name of the plugin-asset subfolder created under a custom model-storage root.</summary>
    public const string PluginDataFolderName = "PluginData";

    public static string DefaultModelStoragePath => TypeWhisperEnvironment.ModelsPath;

    /// <summary>
    /// Resolves the active local model storage path.
    /// </summary>
    public static string ResolveModelStoragePath(AppSettings settings) =>
        AppSettings.NormalizeLocalModelStoragePath(settings.LocalModelStoragePath) is { } customPath
            ? Path.GetFullPath(customPath)
            : DefaultModelStoragePath;

    /// <summary>
    /// Resolves the active plugin asset directory for large model and runtime files.
    /// </summary>
    public static string ResolvePluginAssetDirectory(AppSettings? settings, string pluginId)
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
            return Path.Join(TypeWhisperEnvironment.PluginDataPath, safePluginId);
        }

        return Path.Join(
            ResolveModelStoragePath(settings),
            PluginDataFolderName,
            safePluginId);
    }
}
