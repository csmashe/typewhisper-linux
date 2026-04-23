using System.Diagnostics;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Linux.Services;

public sealed class SpeechFeedbackService
{
    private readonly ISettingsService _settings;
    private readonly SystemCommandAvailabilityService _commands;
    private Process? _currentProcess;

    public SpeechFeedbackService(ISettingsService settings, SystemCommandAvailabilityService commands)
    {
        _settings = settings;
        _commands = commands;
    }

    public bool IsAvailable => _commands.HasSpeechFeedback;
    public string? BackendName => _commands.SpeechFeedbackCommand;

    public void AnnounceRecordingStarted() => Speak("Recording");

    public void AnnounceTranscriptionComplete(string text) => Speak(text);

    public void AnnounceError(string reason) => Speak($"Error: {reason}");

    public void Stop()
    {
        try
        {
            if (_currentProcess is { HasExited: false })
                _currentProcess.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort.
        }
        finally
        {
            _currentProcess?.Dispose();
            _currentProcess = null;
        }
    }

    private void Speak(string text)
    {
        if (!_settings.Current.SpokenFeedbackEnabled || string.IsNullOrWhiteSpace(text))
            return;

        var command = _commands.SpeechFeedbackCommand;
        if (command is null)
            return;

        try
        {
            Stop();
            var startInfo = new ProcessStartInfo(command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (command == "spd-say")
            {
                startInfo.ArgumentList.Add(text);
            }
            else
            {
                startInfo.ArgumentList.Add("--stdout");
                startInfo.ArgumentList.Add(text);
            }

            _currentProcess = command == "spd-say"
                ? Process.Start(startInfo)
                : StartEspeakPlayback(startInfo);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SpeechFeedback] {ex.Message}");
        }
    }

    private static Process? StartEspeakPlayback(ProcessStartInfo espeakStartInfo)
    {
        var player = ResolvePlayer();
        if (player is null)
            return Process.Start(espeakStartInfo);

        var shell = new ProcessStartInfo("sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        shell.ArgumentList.Add("-c");
        shell.ArgumentList.Add($"{Quote(espeakStartInfo.FileName)} --stdout \"$1\" | {player}");
        shell.ArgumentList.Add("sh");
        shell.ArgumentList.Add(espeakStartInfo.ArgumentList.LastOrDefault() ?? "");
        return Process.Start(shell);
    }

    private static string? ResolvePlayer()
    {
        if (CommandExists("paplay")) return "paplay";
        if (CommandExists("aplay")) return "aplay";
        return null;
    }

    private static bool CommandExists(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return false;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (File.Exists(Path.Combine(dir, name)))
                return true;
        }

        return false;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
