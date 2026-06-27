using System.Diagnostics;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
///     Shared subprocess runner for active-window providers. Uses
///     <see cref="Process.WaitForExitAsync" /> for true cancellation: when the
///     per-provider budget fires, the process tree is killed so a hung compositor
///     helper can't block the UI thread's detection loop.
/// </summary>
internal static class ProviderProcessRunner
{
    public static Task<(int ExitCode, string? StdOut)> RunAsync(
        string fileName,
        string args,
        CancellationToken ct
    )
    {
        var psi = new ProcessStartInfo(fileName, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        return RunAsync(psi, ct);
    }

    /// <summary>
    ///     Argv-style overload — passes each argument as a discrete argv entry so
    ///     spaces, quotes, or shell metacharacters in values (e.g. window IDs) are
    ///     not reinterpreted as additional arguments.
    /// </summary>
    public static Task<(int ExitCode, string? StdOut)> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken ct
    )
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        return RunAsync(psi, ct);
    }

    private static async Task<(int ExitCode, string? StdOut)> RunAsync(
        ProcessStartInfo psi,
        CancellationToken ct
    )
    {
        Process? p = null;
        try
        {
            p = Process.Start(psi);
            if (p is null)
            {
                return (-1, null);
            }

            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);

            try
            {
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    p.Kill(true);
                }
                catch
                {
                    /* best effort */
                }

                return (-1, null);
            }

            string? stdout = null;
            try
            {
                stdout = await stdoutTask.ConfigureAwait(false);
            }
            catch
            {
                // Best-effort drain; the process already exited, so a read failure is non-fatal.
            }

            try
            {
                _ = await stderrTask.ConfigureAwait(false);
            }
            catch
            {
                // Best-effort drain; the process already exited, so a read failure is non-fatal.
            }

            return (p.ExitCode, stdout);
        }
        catch
        {
            return (-1, null);
        }
        finally
        {
            p?.Dispose();
        }
    }
}