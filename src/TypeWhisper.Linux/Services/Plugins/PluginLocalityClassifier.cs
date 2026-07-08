using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services.Plugins;

/// <summary>
///     Infers whether a plugin runs on-device or calls out to the network.
///     Manifests may omit <see cref="PluginManifest.IsLocal" /> (e.g. the bundled
///     "Gemma 4 (Local)"), so this falls back to known-ID lists and then to
///     name/description keyword heuristics. Shared by the Plugins settings badges
///     and the history Inspect provenance ("Stayed on this machine") so both agree
///     on whether a call left the machine.
/// </summary>
public static class PluginLocalityClassifier
{
    private static readonly HashSet<string> s_knownLocalPluginIds =
    [
        "com.typewhisper.whisper-cpp",
        "com.typewhisper.sherpa-onnx",
        "com.typewhisper.file-memory",
        "com.typewhisper.obsidian",
        "com.typewhisper.script",
        "com.typewhisper.webhook"
    ];

    private static readonly HashSet<string> s_knownCloudPluginIds =
    [
        "com.typewhisper.assemblyai",
        "com.typewhisper.cerebras",
        "com.typewhisper.claude",
        "com.typewhisper.cloudflare-asr",
        "com.typewhisper.cohere",
        "com.typewhisper.deepgram",
        "com.typewhisper.fireworks",
        "com.typewhisper.gemini",
        "com.typewhisper.gladia",
        "com.typewhisper.google-cloud-stt",
        "com.typewhisper.groq",
        "com.typewhisper.linear",
        "com.typewhisper.openai",
        "com.typewhisper.openai-compatible",
        "com.typewhisper.openrouter",
        "com.typewhisper.qwen3-stt",
        "com.typewhisper.soniox",
        "com.typewhisper.speechmatics",
        "com.typewhisper.voxtral"
    ];

    // Third-party plugins may not set IsLocal; fall back to known-ID lists then keyword heuristics.
    public static bool IsLocal(PluginManifest manifest)
    {
        if (manifest.IsLocal)
        {
            return true;
        }

        var id = manifest.Id.Trim().ToLowerInvariant();
        if (s_knownLocalPluginIds.Contains(id))
        {
            return true;
        }

        if (s_knownCloudPluginIds.Contains(id))
        {
            return false;
        }

        var combined = $"{manifest.Name} {manifest.Description}".ToLowerInvariant();
        return combined.Contains("offline")
               || combined.Contains("local")
               || combined.Contains("on-device")
               || combined.Contains("on device")
               || combined.Contains("file-based")
               || combined.Contains("file based")
               || combined.Contains("obsidian")
               || combined.Contains("shell script")
               || combined.Contains("webhook");
    }
}
