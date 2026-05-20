using System.Diagnostics;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
/// Shared subprocess runner for active-window providers. Crucially, this is
/// truly cancellation-aware: <see cref="Process.WaitForExitAsync"/> observes
/// the caller's <see cref="CancellationToken"/>, so the orchestrator's
/// per-provider budget actually bounds wall-clock time. When cancellation
/// fires, the process tree is killed so a hung compositor helper cannot
/// block the UI thread that owns the timer-driven detection loop.
/// </summary>
internal static class ProviderProcessRunner
{
    public static Task<(int ExitCode, string? StdOut)> RunAsync(
        string fileName, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        return RunAsync(psi, ct);
    }

    /// <summary>
    /// Argv-style overload — each argument is passed as a separate argv entry
    /// so values that might contain spaces, quotes, or shell metacharacters
    /// (e.g. window IDs returned by an external helper) cannot be reinterpreted
    /// as additional arguments.
    /// </summary>
    public static Task<(int ExitCode, string? StdOut)> RunAsync(
        string fileName, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        return RunAsync(psi, ct);
    }

    private static async Task<(int ExitCode, string? StdOut)> RunAsync(
        ProcessStartInfo psi, CancellationToken ct)
    {
        Process? p = null;
        try
        {
            p = Process.Start(psi);
            if (p is null) return (-1, null);

            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);

            try
            {
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (-1, null);
            }

            string? stdout = null;
            try { stdout = await stdoutTask.ConfigureAwait(false); } catch { }
            try { _ = await stderrTask.ConfigureAwait(false); } catch { }
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
