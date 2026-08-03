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
    /// <param name="ct">
    ///     Cancels the run: the process tree is killed and an
    ///     <see cref="OperationCanceledException" /> is thrown. Cancellation is never reported as a
    ///     result, so <see cref="ProcessRunResult.Started" /> stays a statement about the launch.
    /// </param>
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
    private static readonly TimeSpan s_minimumDrainGrace = TimeSpan.FromMilliseconds(250);

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

            using var timeoutCts = timeout is not null
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            var timeoutStopwatch = timeout is not null ? Stopwatch.StartNew() : null;
            if (timeout is { } limit)
            {
                timeoutCts!.CancelAfter(limit);
            }

            var lifecycleToken = timeoutCts?.Token ?? ct;

            using var standardOutputReader = process.StandardOutput;
            using var standardErrorReader = process.StandardError;
            // Drain both pipes from the start so a chatty process cannot fill its output
            // buffer and deadlock against our stdin write below.
            var stdoutTask = standardOutputReader.ReadToEndAsync(ct);
            var stderrTask = standardErrorReader.ReadToEndAsync(ct);

            // Deliberately not `await using`: disposing a writer whose content was never drained
            // flushes into the pipe, and once the process is killed that flush throws. That
            // exception would replace the in-flight cancellation (or a TimedOut return) and end
            // up misreported as NotStarted by the outer catch.
            var standardInputWriter = standardInput is not null ? process.StandardInput : null;
            try
            {
                if (standardInput is not null)
                {
                    try
                    {
                        await standardInputWriter!
                            .WriteAsync(standardInput.AsMemory(), lifecycleToken)
                            .ConfigureAwait(false);
                        standardInputWriter.Close();
                    }
                    catch (OperationCanceledException) when (
                        timeoutCts?.IsCancellationRequested == true && !ct.IsCancellationRequested
                    )
                    {
                        KillProcessTree(process);
                        AbandonRead(standardOutputReader, stdoutTask);
                        AbandonRead(standardErrorReader, stderrTask);
                        return TimedOutResult();
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // Kill before unwinding so the blocked write cannot hold the pipe open.
                        KillProcessTree(process);
                        throw;
                    }
                }

                try
                {
                    await process.WaitForExitAsync(lifecycleToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    timeoutCts?.IsCancellationRequested == true && !ct.IsCancellationRequested
                )
                {
                    // Inner timeout fired (not the caller's ct) — kill and return TimedOut
                    // so the caller can distinguish a timeout from a hard cancellation.
                    KillProcessTree(process);
                    AbandonRead(standardOutputReader, stdoutTask);
                    AbandonRead(standardErrorReader, stderrTask);
                    return TimedOutResult();
                }

                var exitCode = process.ExitCode;
                if (timeout is not { } timeoutLimit)
                {
                    return new ProcessRunResult(
                        true,
                        false,
                        exitCode,
                        await stdoutTask.ConfigureAwait(false),
                        await stderrTask.ConfigureAwait(false)
                    );
                }

                var remaining = timeoutLimit - timeoutStopwatch!.Elapsed;
                // Preserve the lifecycle deadline when time remains, but allow a small
                // post-exit grace so a process exiting at the deadline can flush normal
                // redirected output. The total run may therefore exceed the limit by at
                // most 250 ms when the process exits at deadline-minus-epsilon.
                var drainLimit = remaining > s_minimumDrainGrace ? remaining : s_minimumDrainGrace;
                using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                drainCts.CancelAfter(drainLimit);
                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask)
                        .WaitAsync(drainCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // The process itself exited, so its exit code is authoritative. A
                    // descendant may still hold the pipe writers (wl-copy/xclip do this).
                    // Close our redirected stream handles and observe any resulting
                    // background read faults rather than surfacing a false process timeout
                    // or waiting for the descendant.
                    AbandonRead(standardOutputReader, stdoutTask);
                    AbandonRead(standardErrorReader, stderrTask);
                }

                return new ProcessRunResult(
                    true,
                    false,
                    exitCode,
                    CompletedOutput(stdoutTask),
                    CompletedOutput(stderrTask)
                );
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller cancellation: kill the tree per the documented contract and let the
                // cancellation surface, rather than reporting a started process as NotStarted.
                KillProcessTree(process);
                AbandonRead(standardOutputReader, stdoutTask);
                AbandonRead(standardErrorReader, stderrTask);
                throw;
            }
            finally
            {
                AbandonStandardInput(standardInputWriter);
            }
        }
        catch (OperationCanceledException)
        {
            // Must precede the generic catch, which would otherwise flatten cancellation
            // into a NotStarted result.
            throw;
        }
        catch (Exception ex)
        {
            return ProcessRunResult.NotStarted(ex.Message);
        }
    }

    private static string CompletedOutput(Task<string> readTask)
    {
        return readTask.Status == TaskStatus.RanToCompletion
            ? readTask.Result
            : string.Empty;
    }

    private static void AbandonStandardInput(StreamWriter? writer)
    {
        try
        {
            writer?.Dispose();
        }
        catch
        {
            // A broken pipe (the process was killed with our write still buffered) must not
            // displace the cancellation or timeout result this call is already returning.
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch
        {
            /* best effort */
        }
    }

    private static void AbandonRead(StreamReader reader, Task readTask)
    {
        ObserveFault(readTask);
        try
        {
            // Process.Dispose does not close a redirected stream once its reader
            // has been accessed; close the caller-owned pipe handle explicitly.
            reader.BaseStream.Close();
        }
        catch
        {
            /* best effort */
        }
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private static ProcessRunResult TimedOutResult()
    {
        return new ProcessRunResult(
            true,
            true,
            -1,
            string.Empty,
            string.Empty
        );
    }
}
