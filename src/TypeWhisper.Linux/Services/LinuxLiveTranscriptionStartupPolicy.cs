using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services;

internal enum LiveTranscriptionMode
{
    None,
    Polling,
    Streaming
}

// Decides whether the recording loop should run the live-transcription preview
// poll or the websocket streaming path. Ported from upstream's
// LiveTranscriptionStartupPolicy (7447cdc); the fork grew the Streaming arm in
// C5 once the host-side websocket subsystem landed (scope:
// docs/plans/2026-05-22-websocket-streaming-subsystem.md).
internal static class LinuxLiveTranscriptionStartupPolicy
{
    public static LiveTranscriptionMode Select(
        AppSettings settings,
        ITranscriptionEnginePlugin? plugin)
    {
        if (!settings.LiveTranscriptionEnabled)
        {
            return LiveTranscriptionMode.None;
        }

        if (plugin is null)
        {
            return LiveTranscriptionMode.None;
        }

        // Real-time websocket streaming wins over polling when the plugin
        // supports it and the user opted in. Strictly cheaper than batch
        // re-upload (latency in hundreds of ms vs 3 s poll cadence; each
        // chunk sent once vs the whole growing buffer re-sent every poll).
        if (plugin.SupportsStreaming && settings.LiveTranscriptionStreamingEnabled)
        {
            return LiveTranscriptionMode.Streaming;
        }

        // Local downloadable models transcribe on-device — polling the partial
        // preview is cheap.
        if (plugin.SupportsModelDownload)
        {
            return LiveTranscriptionMode.Polling;
        }

        // Cloud/online providers: each partial poll re-uploads the whole
        // growing buffer. Off unless the user opts in.
        if (settings.OnlineAsrBatchLiveTranscriptionEnabled)
        {
            return LiveTranscriptionMode.Polling;
        }

        return LiveTranscriptionMode.None;
    }
}
