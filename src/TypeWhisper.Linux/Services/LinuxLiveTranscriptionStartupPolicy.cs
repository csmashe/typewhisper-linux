using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services;

internal enum LiveTranscriptionMode
{
    None,
    Polling
}

// Decides whether the recording loop should run the live-transcription preview
// poll. Ported from upstream's LiveTranscriptionStartupPolicy (7447cdc),
// simplified to the fork's single live mechanism: the orchestrator only ever
// polls (no websocket streaming path exists here).
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

        // Local downloadable models transcribe on-device — polling the partial
        // preview is cheap.
        if (plugin.SupportsModelDownload)
        {
            return LiveTranscriptionMode.Polling;
        }

        // Cloud/online providers: each partial poll re-uploads the whole
        // growing buffer. (SupportsStreaming is deliberately ignored — the
        // Linux fork polls; it has no real websocket streaming path.) Off
        // unless the user opts in.
        if (settings.OnlineAsrBatchLiveTranscriptionEnabled)
        {
            return LiveTranscriptionMode.Polling;
        }

        return LiveTranscriptionMode.None;
    }
}
