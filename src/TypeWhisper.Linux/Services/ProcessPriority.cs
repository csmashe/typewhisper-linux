using System.Diagnostics;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Resets CPU + I/O scheduling priority on the calling process.
/// </summary>
/// <remarks>
///     GNOME (and other systemd-based DEs) launches menu-clicked apps under a
///     transient scope with <c>nice=6</c> and <c>ionice idle</c> to keep the
///     shell responsive while a heavy app starts. For a .NET app that needs
///     CPU + disk during startup (JIT, R2R image faulting, DI container build)
///     this throttles cold start by ~60×. The same binary launched from a
///     terminal inherits the terminal's nice 0 / best-effort I/O and starts in
///     well under 2s.
///
///     We shell out to <c>renice</c> and <c>ionice</c> against our own PID
///     instead of P/Invoking <c>setpriority</c>/<c>ioprio_set</c>. The C
///     wrappers have signed-int return-vs-error ambiguity (getpriority can
///     legally return -1) and the raw <c>syscall(2)</c> takes <c>long</c>
///     arguments which P/Invoke marshals incorrectly with <c>int</c>.
///     The external tools are a few milliseconds each and not worth getting
///     wrong.
///
///     Failures are non-fatal — the app keeps starting; we just log so we know.
/// </remarks>
internal static class ProcessPriority
{
    public static string ResetToDefaults()
    {
        var pid = Environment.ProcessId.ToString();
        var results = new List<string>();

        results.Add(Run("renice", $"-n 0 -p {pid}"));
        results.Add(Run("ionice", $"-c 2 -n 4 -p {pid}"));

        return string.Join("; ", results);
    }

    private static string Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(
                new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            );
            if (p is null)
            {
                return $"{file}: failed to start";
            }

            // 1s cap is plenty; these are local tools doing one syscall.
            if (!p.WaitForExit(1000))
            {
                try
                {
                    p.Kill();
                }
                catch
                {
                    /* best effort */
                }

                return $"{file}: timed out";
            }

            if (p.ExitCode == 0)
            {
                return $"{file}: ok";
            }

            var err = p.StandardError.ReadToEnd().Trim();
            return $"{file}: exit {p.ExitCode}{(err.Length > 0 ? " — " + err : "")}";
        }
        catch (Exception ex)
        {
            return $"{file}: {ex.GetType().Name} — {ex.Message}";
        }
    }
}
