using System.Diagnostics;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// Outcome of a process invocation through <see cref="IProcessRunner"/>.
/// <see cref="Started"/> is false when the process could not be launched at
/// all (bad path, fork failure); <see cref="TimedOut"/> is true when it
/// launched but outlived its timeout and was killed.
/// </summary>
public sealed record ProcessRunResult(
    bool Started,
    bool TimedOut,
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    /// <summary>True only when the process ran to completion with exit code 0.</summary>
    public bool Succeeded => Started && !TimedOut && ExitCode == 0;

    internal static ProcessRunResult NotStarted(string error) =>
        new(Started: false, TimedOut: false, ExitCode: -1,
            StandardOutput: string.Empty, StandardError: error);
}

/// <summary>
/// Seam over <see cref="System.Diagnostics.Process"/>. Process-orchestrating
/// services — the ones whose real logic is ownership gating, command
/// ordering, and branch-on-failure handling — depend on this interface so
/// that logic can be unit-tested with a recording fake instead of spawning
/// real subprocesses. The production implementation is <see cref="ProcessRunner"/>;
/// the only thing left "verified manually" is that thin wrapper itself.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Run <paramref name="fileName"/> with <paramref name="args"/> (passed as
    /// a real argv — no shell, no quoting), capturing stdout and stderr.
    /// </summary>
    /// <param name="environment">Extra variables merged onto the inherited environment.</param>
    /// <param name="standardInput">When non-null, written to the process's stdin, which is then closed.</param>
    /// <param name="timeout">When set, the process tree is killed if it outlives the window and the result is flagged <see cref="ProcessRunResult.TimedOut"/>.</param>
    Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment = null,
        string? standardInput = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default);
}

/// <summary>
/// Production <see cref="IProcessRunner"/> — a deliberately logic-free
/// wrapper over <see cref="Process"/>. All conditional behavior lives in the
/// callers (tested against a fake); this type only spawns, feeds, drains,
/// times out, and reports.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment = null,
        string? standardInput = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return ProcessRunResult.NotStarted($"Could not start {fileName}");

            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), ct).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            if (timeout is { } limit)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(limit);
                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    return new ProcessRunResult(
                        Started: true, TimedOut: true, ExitCode: -1,
                        StandardOutput: string.Empty, StandardError: string.Empty);
                }
            }
            else
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }

            return new ProcessRunResult(
                Started: true,
                TimedOut: false,
                ExitCode: process.ExitCode,
                StandardOutput: await stdoutTask.ConfigureAwait(false),
                StandardError: await stderrTask.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return ProcessRunResult.NotStarted(ex.Message);
        }
    }
}
