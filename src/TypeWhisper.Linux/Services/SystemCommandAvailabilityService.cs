using System.IO;

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
}
