using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
///     Active-window domain adapter: caller cancellation is a provider miss, after
///     the central supervisor has killed and reaped the owned process tree.
/// </summary>
internal sealed class ProviderProcessRunner(IProcessRunner processRunner)
{
    public async Task<(int ExitCode, string? StdOut)> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken ct
    )
    {
        try
        {
            var result = await processRunner.RunOneShotAsync(
                    new ProcessCommand(fileName, args),
                    new ProcessOneShotOptions(
                        StandardError: ProcessCaptureMode.Discard
                    ),
                    ct
                )
                .ConfigureAwait(false);
            return result.Status == ProcessRunStatus.Exited
                ? (result.ExitCode ?? -1, result.StandardOutputText)
                : (-1, null);
        }
        catch (OperationCanceledException)
        {
            return (-1, null);
        }
        catch
        {
            return (-1, null);
        }
    }
}
