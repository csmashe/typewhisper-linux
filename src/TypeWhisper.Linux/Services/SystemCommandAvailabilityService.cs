using System.IO;
using System.Diagnostics;

namespace TypeWhisper.Linux.Services;

public sealed class SystemCommandAvailabilityService
{
    public bool HasPactl => IsCommandAvailable("pactl");
    public bool HasPlayerCtl => IsCommandAvailable("playerctl");
    public bool HasCanberraGtkPlay => IsCommandAvailable("canberra-gtk-play");
    public bool HasFfmpeg => IsCommandAvailable("ffmpeg");
    public bool HasSpeechFeedback => IsCommandAvailable("espeak-ng")
                                    || IsCommandAvailable("espeak")
                                    || IsCommandAvailable("spd-say");
    public bool HasCudaGpu => IsCommandAvailable("nvidia-smi") || File.Exists("/dev/nvidiactl");
    public bool HasCudaRuntimeLibraries => IsLibraryAvailable("libcudart.so.12")
                                           && IsLibraryAvailable("libcublas.so.12");

    public string? SpeechFeedbackCommand
    {
        get
        {
            if (IsCommandAvailable("espeak-ng")) return "espeak-ng";
            if (IsCommandAvailable("espeak")) return "espeak";
            if (IsCommandAvailable("spd-say")) return "spd-say";
            return null;
        }
    }

    private static bool IsCommandAvailable(string commandName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            return false;

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, commandName);
                if (File.Exists(candidate))
                    return true;
            }
            catch
            {
                // Ignore invalid PATH entries.
            }
        }

        return false;
    }

    private static bool IsLibraryAvailable(string libraryName)
    {
        if (FindInLdCache(libraryName))
            return true;

        foreach (var directory in new[]
                 {
                     "/usr/local/cuda/lib64",
                     "/usr/local/cuda-13.0/lib64",
                     "/usr/lib/x86_64-linux-gnu",
                     "/lib/x86_64-linux-gnu"
                 })
        {
            try
            {
                if (File.Exists(Path.Combine(directory, libraryName)))
                    return true;
            }
            catch
            {
                // Ignore inaccessible library directories.
            }
        }

        return false;
    }

    private static bool FindInLdCache(string libraryName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ldconfig", "-p")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
                return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1000);
            return output.Contains(libraryName, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
