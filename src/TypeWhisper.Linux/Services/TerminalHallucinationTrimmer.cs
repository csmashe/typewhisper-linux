using System.Diagnostics.CodeAnalysis;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

internal static class TerminalHallucinationTrimmer
{
    // Null-tolerant: a plugin may violate the non-null contract, and the orchestrator's
    // preview fallback must still get its chance on that path.
    [return: NotNullIfNotNull(nameof(result))]
    internal static PluginTranscriptionResult? Trim(PluginTranscriptionResult? result)
    {
        if (result is null || result.Segments.Count == 0)
        {
            return result;
        }

        var terminalSegment = result.Segments[^1];
        return WhisperHallucinationFilter.TryStripTerminalSegment(
            result.Text,
            terminalSegment.Text,
            terminalSegment.NoSpeechProbability,
            out var strippedText)
            ? result with
            {
                Text = strippedText,
                Segments = result.Segments.Take(result.Segments.Count - 1).ToArray(),
            }
            : result;
    }
}
