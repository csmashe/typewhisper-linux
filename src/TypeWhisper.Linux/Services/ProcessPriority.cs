using System.Diagnostics;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Resets CPU + I/O scheduling priority on the calling process.
/// </summary>
/// <remarks>
///     GNOME (and other systemd-based DEs) launches menu-clicked apps under a transient scope with
///     nice=6 and ionice idle, which throttles cold start of a .NET app by ~60× (JIT, R2R faulting,
///     DI build). Terminal launches inherit nice 0 / best-effort and start normally.
///     Shells out to renice/ionice rather than P/Invoking setpriority/ioprio_set because getpriority
///     legally returns -1 (ambiguous error vs value) and the raw syscall takes long args that
///     P/Invoke marshals incorrectly. Failures are non-fatal; the app keeps starting.
/// </remarks>
internal static class ProcessPriority
{
    public static string ResetToDefaults()
    {
        var pid = Environment.ProcessId.ToString();
        var results = new List<string>
        {
            Run("renice", $"-n 0 -p {pid}"),
            Run("ionice", $"-c 2 -n 4 -p {pid}")
        };

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