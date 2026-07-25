using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services.Plugins;

/// <summary>
///     Compatibility-only locality fallback for external manifests that predate
///     <see cref="PluginManifest.NetworkAccess" />. New metadata is normalized once
///     by <see cref="PluginLoader" /> and consumers must use the resulting descriptor.
///     Unknown plugins fail closed to <see cref="PluginNetworkAccess.Network" />.
/// </summary>
internal static class PluginLocalityClassifier
{
    private static readonly HashSet<string> s_knownLocalPluginIds =
    [
        "com.typewhisper.whisper-cpp",
        "com.typewhisper.sherpa-onnx",
        "com.typewhisper.gemma-local",
        "com.typewhisper.file-memory",
        "com.typewhisper.obsidian",
    ];

    public static PluginNetworkAccess ResolveLegacy(PluginManifest manifest)
    {
        if (manifest.IsLocal is { } declaredIsLocal)
        {
            return declaredIsLocal
                ? PluginNetworkAccess.Local
                : PluginNetworkAccess.Network;
        }

        return s_knownLocalPluginIds.Contains(
            manifest.Id.Trim().ToLowerInvariant()
        )
            ? PluginNetworkAccess.Local
            : PluginNetworkAccess.Network;
    }
}
