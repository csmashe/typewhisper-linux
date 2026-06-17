using System.Diagnostics;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Runs a short-lived CLI tool synchronously and returns its trimmed stdout,
///     or <c>null</c> on any failure (couldn't start, non-zero exit, exception).
///     Forces <c>LC_ALL=C</c> so output is stable and parseable regardless of the
///     user's locale.
///
///     This is a deliberately simple, fire-and-forget capture for fast helpers such
///     as <c>playerctl</c> and <c>pactl</c>. Services that need cancellation, stdin,
///     timeout reporting, or a testable seam should use <see cref="IProcessRunner" />
///     instead.
/// </summary>
internal static class CommandRunner
{
    public static string? Run(string fileName, params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            // Force a stable, parseable locale for command output.
            psi.Environment["LC_ALL"] = "C";

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1500);
            return process.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
