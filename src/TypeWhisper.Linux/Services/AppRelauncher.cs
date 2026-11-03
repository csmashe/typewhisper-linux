using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Starts the app again once this instance has exited, so a reset requested in the running app
///     is carried out by a fresh startup.
/// </summary>
internal static class AppRelauncher
{
    private const string WaitForProcessVariable = "TYPEWHISPER_RELAUNCH_AFTER_PID";
    private static readonly TimeSpan s_waitBudget = TimeSpan.FromSeconds(15);

    /// <summary>Starts a new instance that waits for this process to exit. Never throws.</summary>
    public static void Relaunch()
    {
        var command = ResolveCommand(
            Environment.GetEnvironmentVariable("APPIMAGE"),
            Environment.ProcessPath,
            Assembly.GetEntryAssembly()?.Location
        );
        if (command is null)
        {
            Trace.WriteLine("[AppRelauncher] No executable to restart; reopen TypeWhisper to finish.");
            return;
        }

        var outcome = new ProcessRunner().LaunchDetached(
            command with
            {
                Environment = new Dictionary<string, string>
                {
                    [WaitForProcessVariable] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                },
            }
        );
        if (!outcome.Started)
        {
            Trace.WriteLine("[AppRelauncher] Restart failed; reopen TypeWhisper to finish.");
        }
    }

    /// <summary>
    ///     In a relaunched instance, waits until the instance that started it has exited, so the
    ///     single-instance check and the reset do not meet it still shutting down.
    /// </summary>
    public static void WaitForPreviousInstance()
    {
        var value = Environment.GetEnvironmentVariable(WaitForProcessVariable);
        if (value is null)
        {
            return;
        }

        // Not passed on to plugins, workers or a later relaunch.
        Environment.SetEnvironmentVariable(WaitForProcessVariable, null);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || pid == Environment.ProcessId)
        {
            return;
        }

        var deadline = Stopwatch.StartNew();
        while (Directory.Exists($"/proc/{pid}") && deadline.Elapsed < s_waitBudget)
        {
            Thread.Sleep(100);
        }
    }

    /// <summary>
    ///     The command that starts the app: the AppImage (its mount goes away with this process), the
    ///     app executable, or the <c>dotnet</c> host with the app's assembly in a development run.
    /// </summary>
    internal static ProcessCommand? ResolveCommand(string? appImage, string? processPath, string? entryAssembly)
    {
        if (!string.IsNullOrEmpty(appImage) && File.Exists(appImage))
        {
            return new ProcessCommand(appImage, []);
        }

        if (string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        if (!string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.Ordinal))
        {
            return new ProcessCommand(processPath, []);
        }

        return string.IsNullOrEmpty(entryAssembly) ? null : new ProcessCommand(processPath, [entryAssembly]);
    }
}
