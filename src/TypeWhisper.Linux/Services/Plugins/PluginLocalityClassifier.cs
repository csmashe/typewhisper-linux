using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services.Plugins;

/// <summary>
///     Decides whether a plugin runs on-device or calls out to the network.
///     Shared by the Plugins settings badges and the history Inspect provenance
///     ("Stayed on this machine") so both agree on whether a call left the machine.
///     Classification is deterministic: a manifest's explicit
///     <see cref="PluginManifest.IsLocal" /> flag or a known on-device id. Bundled
///     local plugins that omit the flag (e.g. "Gemma 4 (Local)") are listed
///     explicitly. Anything else defaults to non-local — for a privacy badge,
///     wrongly claiming a call stayed on-device is worse than wrongly showing that
///     it was sent to a provider, so locality is never inferred from free-text
///     name/description keywords (which a cloud plugin could trivially trip).
/// </summary>
public static class PluginLocalityClassifier
{
    private static readonly HashSet<string> s_knownLocalPluginIds =
    [
        "com.typewhisper.whisper-cpp",
        "com.typewhisper.sherpa-onnx",
        "com.typewhisper.gemma-local",
        "com.typewhisper.file-memory",
        "com.typewhisper.obsidian",
        "com.typewhisper.script",
        "com.typewhisper.webhook",
    ];

    public static bool IsLocal(PluginManifest manifest) =>
        manifest.IsLocal
        || s_knownLocalPluginIds.Contains(manifest.Id.Trim().ToLowerInvariant());
}
