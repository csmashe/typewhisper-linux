using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services;

internal enum LiveTranscriptionMode
{
    None,
    Polling,
    Streaming,
}

// Selects the live-transcription mode for the recording loop. Ported from
// upstream's LiveTranscriptionStartupPolicy (7447cdc); the Streaming arm was
// added when the websocket subsystem landed (docs/plans/2026-05-22-websocket-streaming-subsystem.md).
internal static class LinuxLiveTranscriptionStartupPolicy
{
    public static LiveTranscriptionMode Select(
        AppSettings settings,
        ITranscriptionEngineRole? plugin)
    {
        if (!settings.LiveTranscriptionEnabled || plugin is null)
        {
            return LiveTranscriptionMode.None;
        }

        // Streaming wins over polling when the plugin supports it and the user
        // opted in: each chunk is sent once vs the whole growing buffer
        // re-sent every 3 s poll cycle.
        if (plugin.SupportsStreaming && settings.LiveTranscriptionStreamingEnabled)
        {
            return LiveTranscriptionMode.Streaming;
        }

        // Local/on-device models: polling the partial preview is cheap.
        if (plugin.SupportsModelDownload)
        {
            return LiveTranscriptionMode.Polling;
        }

        // Cloud providers re-upload the whole growing buffer on each poll — off unless opted in.
        return settings.OnlineAsrBatchLiveTranscriptionEnabled
            ? LiveTranscriptionMode.Polling
            : LiveTranscriptionMode.None;
    }
}
