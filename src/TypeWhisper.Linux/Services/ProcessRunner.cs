using System.Diagnostics;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Outcome of a process invocation through <see cref="IProcessRunner" />.
///     <see cref="Started" /> is false when the process could not be launched at
///     all (bad path, fork failure); <see cref="TimedOut" /> is true when it
///     launched but outlived its timeout and was killed.
/// </summary>
public sealed record ProcessRunResult(
    bool Started,
    bool TimedOut,
    int ExitCode,
    string StandardOutput,
    string StandardError
)
{
    /// <summary>True only when the process ran to completion with exit code 0.</summary>
    public bool Succeeded => Started && !TimedOut && ExitCode == 0;

    internal static ProcessRunResult NotStarted(string error)
    {
        return new ProcessRunResult(
            false,
            false,
            -1,
            string.Empty,
            error
        );
    }
}

/// <summary>
///     Seam over <see cref="System.Diagnostics.Process" /> so orchestrating services
///     (ownership gating, command ordering, branch-on-failure) can be unit-tested
///     with a recording fake. The production implementation is <see cref="ProcessRunner" />.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    ///     Run <paramref name="fileName" /> with <paramref name="args" /> (passed as
    ///     a real argv — no shell, no quoting), capturing stdout and stderr.
    /// </summary>
    /// <param name="fileName">Executable to run, resolved via PATH (no shell).</param>
    /// <param name="args">Arguments passed as a real argv — no shell, no quoting.</param>
    /// <param name="environment">Extra variables merged onto the inherited environment.</param>
    /// <param name="standardInput">When non-null, written to the process's stdin, which is then closed.</param>
    /// <param name="timeout">
    ///     When set, the process tree is killed if it outlives the window and the result is flagged
    ///     <see cref="ProcessRunResult.TimedOut" />.
    /// </param>
    /// <param name="ct">Cancels the run; the process tree is killed on cancellation.</param>
    Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment = null,
        string? standardInput = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    );
}

/// <summary>
///     Production <see cref="IProcessRunner" /> — a deliberately logic-free wrapper
///     over <see cref="Process" />. All conditional behavior lives in the callers.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment = null,
        string? standardInput = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    )
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                psi.Environment[key] = value;
            }
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return ProcessRunResult.NotStarted($"Could not start {fileName}");
            }

            if (standardInput is not null)
            {
                await process
                    .StandardInput.WriteAsync(standardInput.AsMemory(), ct)
                    .ConfigureAwait(false);
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
                    // Inner timeout fired (not the caller's ct) — kill and return TimedOut
                    // so the caller can distinguish a timeout from a hard cancellation.
                    try
                    {
                        process.Kill(true);
                    }
                    catch
                    {
                        /* best effort */
                    }

                    return new ProcessRunResult(
                        true,
                        true,
                        -1,
                        string.Empty,
                        string.Empty
                    );
                }
            }
            else
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }

            return new ProcessRunResult(
                true,
                false,
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false)
            );
        }
        catch (Exception ex)
        {
            return ProcessRunResult.NotStarted(ex.Message);
        }
    }
}