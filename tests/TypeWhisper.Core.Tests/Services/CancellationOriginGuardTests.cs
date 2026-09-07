using System.Text.RegularExpressions;

namespace TypeWhisper.Core.Tests.Services;

/// <summary>
///     Source-inventory guard for the cancellation hot paths audited in §12 M6.
///     This intentionally does not freeze every production catch in the repository.
/// </summary>
public partial class CancellationOriginGuardTests
{
    private static readonly SourceRegion[] s_regions =
    [
        new(
            "PostProcessingPipeline.ProcessAsync",
            "src/TypeWhisper.Core/Services/PostProcessingPipeline.cs",
            "public async Task<PostProcessingResult> ProcessAsync(",
            "private static string NormalizeSpokenLineBreaks"
        ),
        new(
            "WatchFolderService.ProcessQueueAsync",
            "src/TypeWhisper.Linux/Services/WatchFolderService.cs",
            "private async Task ProcessQueueAsync(WatchFolderRun run)",
            "private async Task ObserveWorkerAsync"
        ),
        new(
            "WatchFolderService.ProcessFileAsync",
            "src/TypeWhisper.Linux/Services/WatchFolderService.cs",
            "private async Task ProcessFileAsync(",
            "private async Task<WatchFolderTranscriptionResult> TranscribeWithReadinessRetryAsync"
        ),
        new(
            "LlmStreamPump.RunAsync",
            "src/TypeWhisper.Linux/Services/LlmStreamPump.cs",
            "public async Task<string> RunAsync(",
            "private void Emit()"
        ),
        new(
            "StreamingTranscriptionCoordinator.FinalizeAsync",
            "src/TypeWhisper.Linux/Services/StreamingTranscriptionCoordinator.cs",
            "public async Task<string> FinalizeAsync(",
            "private string SnapshotFinalSegments()"
        ),
        new(
            "StreamingTranscriptionCoordinator.RunSenderAsync",
            "src/TypeWhisper.Linux/Services/StreamingTranscriptionCoordinator.cs",
            "private async Task RunSenderAsync(",
            "private void OnTranscriptReceived"
        ),
        new(
            "DictationOrchestrator.post-stop terminal block",
            "src/TypeWhisper.Linux/Services/DictationOrchestrator.cs",
            "// Streaming finalize must run BEFORE pad/save",
            "// Safety net: every early-discard branch above"
        ),
        new(
            "DictationOrchestrator.RunPromptActionAsync",
            "src/TypeWhisper.Linux/Services/DictationOrchestrator.cs",
            "private async Task<string> RunPromptActionAsync(",
            "internal static async Task<PromptActionStreamOutcome>"
        ),
        new(
            "DictationOrchestrator spoken-command stream",
            "src/TypeWhisper.Linux/Services/DictationOrchestrator.cs",
            "private async Task<StreamCommandResult> StreamCommandOntoPageAsync(",
            "internal static async Task<bool> RecoverSpokenCommandStreamFaultAsync("
        ),
        new(
            "LlmCleanupService.CleanAsync",
            "src/TypeWhisper.Linux/Services/LlmCleanupService.cs",
            "public async Task<string> CleanAsync(",
            "private static async Task NotifyStatusAsync"
        ),
        new(
            "PromptPaletteService run/batch/action boundaries",
            "src/TypeWhisper.Linux/Services/PromptPaletteService.cs",
            "private async Task ExecuteActionAsync(",
            "private static async Task CloseWindowAsync"
        ),
        new(
            "FileTranscriptionSectionViewModel.ProcessQueueAsync",
            "src/TypeWhisper.Linux/ViewModels/Sections/FileTranscriptionSectionViewModel.cs",
            "private async Task ProcessQueueAsync()",
            "private FileTranscriptionProcessOptions BuildFileTranscriptionOptions()"
        ),
        new(
            "TransformSelectionService processing block",
            "src/TypeWhisper.Linux/Services/TransformSelectionService.cs",
            "private async Task StopAndTransformAsync()",
            "private async Task AbortReplacementAsync"
        ),
    ];

    private static readonly AllowedUnfilteredCatch[] s_allowlist =
    [
        new(
            "WatchFolderService.ProcessQueueAsync",
            1,
            "The unfiltered catch only exits a locally-owned debounce delay; queue-item cancellation is filtered separately."
        ),
        new(
            "StreamingTranscriptionCoordinator.FinalizeAsync",
            2,
            "One provider OCE is deliberately recorded as a finalize fault; one locally-owned grace-delay OCE only ends the grace wait."
        ),
        new(
            "DictationOrchestrator.RunPromptActionAsync",
            1,
            "This catch is transparent and rethrows; LlmStreamPump owns the origin classification and fallback signal."
        ),
    ];

    [Fact]
    public void Hot_methods_have_no_unexplained_unfiltered_cancellation_catches()
    {
        var root = FindRepositoryRoot();
        var failures = new List<string>();

        foreach (var region in s_regions)
        {
            var source = File.ReadAllText(Path.Join(root, region.RelativePath));
            var snippet = Slice(source, region);
            var unfilteredCount = UnfilteredOperationCanceledCatchRegex().Count(snippet);
            var allowance = s_allowlist.SingleOrDefault(item => item.Region == region.Name);
            var allowedCount = allowance?.Count ?? 0;

            if (allowance is not null && string.IsNullOrWhiteSpace(allowance.Rationale))
            {
                failures.Add($"{region.Name}: allowlist rationale is empty.");
            }

            if (unfilteredCount != allowedCount)
            {
                failures.Add(
                    $"{region.Name}: found {unfilteredCount} unfiltered OCE catch(es), "
                    + $"expected {allowedCount}. Add an origin filter or a narrow rationale."
                );
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Allowlist_names_only_existing_hot_regions()
    {
        Assert.All(
            s_allowlist,
            allowance => Assert.Contains(s_regions, region => region.Name == allowance.Region)
        );
        Assert.Equal(
            s_allowlist.Length,
            s_allowlist.Select(item => item.Region).Distinct(StringComparer.Ordinal).Count()
        );
    }

    private static string Slice(string source, SourceRegion region)
    {
        var start = source.IndexOf(region.StartMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker missing for {region.Name}: {region.StartMarker}");
        var end = source.IndexOf(region.EndMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker missing for {region.Name}: {region.EndMarker}");
        return source[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Join(directory.FullName, "src"))
                && Directory.Exists(Path.Join(directory.FullName, "plugins")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find the repository root from {AppContext.BaseDirectory}."
        );
    }

    // Matches the derived TaskCanceledException too — catching it unfiltered swallows
    // cancellation the same way. The prefix is left open so no qualified spelling slips past.
    [GeneratedRegex(
        @"catch\s*\(\s*(?:[\w.]+\.)?(?:Operation|Task)CanceledException(?:\s+\w+)?\s*\)(?!\s*when)"
    )]
    private static partial Regex UnfilteredOperationCanceledCatchRegex();

    private sealed record SourceRegion(
        string Name,
        string RelativePath,
        string StartMarker,
        string EndMarker
    );

    private sealed record AllowedUnfilteredCatch(string Region, int Count, string Rationale);
}
