using TypeWhisper.PluginSDK.Processes;

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
    public static string ResetToDefaults(IProcessRunner processRunner)
    {
        var pid = Environment.ProcessId.ToString();
        var results = new List<string>
        {
            Run(processRunner, "renice", ["-n", "0", "-p", pid]),
            Run(processRunner, "ionice", ["-c", "2", "-n", "4", "-p", pid]),
        };

        return string.Join("; ", results);
    }

    private static string Run(
        IProcessRunner processRunner,
        string file,
        IReadOnlyList<string> arguments
    )
    {
        try
        {
            var result = processRunner.RunProbe(
                new ProcessCommand(file, arguments),
                new ProcessOneShotOptions(Timeout: TimeSpan.FromSeconds(1))
            );
            // ReSharper disable once ConvertIfStatementToSwitchStatement -- chain continues
            // into ExitCode checks below; a switch would only cover part of it.
            if (result.Status == ProcessRunStatus.StartFailed)
            {
                return $"{file}: failed to start — {result.StartError}";
            }

            // 1s cap is plenty; these are local tools doing one syscall.
            if (result.Status == ProcessRunStatus.TimedOut)
            {
                return $"{file}: timed out";
            }

            if (result.ExitCode == 0)
            {
                return $"{file}: ok";
            }

            var err = result.StandardErrorText.Trim();
            return $"{file}: exit {result.ExitCode}{(err.Length > 0 ? " — " + err : "")}";
        }
        catch (Exception ex)
        {
            return $"{file}: {ex.GetType().Name} — {ex.Message}";
        }
    }
}
