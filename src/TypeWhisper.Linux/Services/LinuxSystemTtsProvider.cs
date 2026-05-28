using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

public sealed class LinuxSystemTtsProvider : ITtsProviderPlugin
{
    public const string BuiltInProviderId = AppSettings.DefaultSpokenFeedbackProviderId;
    private readonly SystemCommandAvailabilityService _commands;

    private readonly ISettingsService _settings;

    public LinuxSystemTtsProvider(
        ISettingsService settings,
        SystemCommandAvailabilityService commands
    )
    {
        _settings = settings;
        _commands = commands;
    }

    public string PluginId => "com.typewhisper.tts.linux-system";
    public string PluginName => "Linux System Voice";
    public string PluginVersion => "1.0.0";
    public string ProviderId => BuiltInProviderId;
    public string ProviderDisplayName => "Linux system voice";
    public bool IsConfigured => _commands.SpeechFeedbackCommand is not null;
    public string? SelectedVoiceId => _settings.Current.SpokenFeedbackVoiceId;
    public string? SettingsSummary => SelectedVoiceId ?? "System default voice";

    public IReadOnlyList<PluginVoiceInfo> AvailableVoices => [];

    public Task ActivateAsync(IPluginHostServices host)
    {
        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        return Task.CompletedTask;
    }

    public void SelectVoice(string? voiceId)
    {
        var normalized = string.IsNullOrWhiteSpace(voiceId) ? null : voiceId;
        if (_settings.Current.SpokenFeedbackVoiceId == normalized)
        {
            return;
        }

        _settings.Save(_settings.Current with { SpokenFeedbackVoiceId = normalized });
    }

    public Task<ITtsPlaybackSession> SpeakAsync(TtsSpeakRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return Task.FromResult<ITtsPlaybackSession>(InactiveTtsPlaybackSession.Instance);
        }

        var command = _commands.SpeechFeedbackCommand;
        if (command is null)
        {
            return Task.FromResult<ITtsPlaybackSession>(InactiveTtsPlaybackSession.Instance);
        }

        ct.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // spd-say (Speech Dispatcher) handles audio output itself; espeak/
        // espeak-ng are used with --stdout and piped into paplay/aplay below.
        if (command == "spd-say")
        {
            startInfo.ArgumentList.Add(request.Text);
            var process = Process.Start(startInfo);
            return Task.FromResult<ITtsPlaybackSession>(
                process is null
                    ? InactiveTtsPlaybackSession.Instance
                    : new ProcessTtsPlaybackSession(process, ct)
            );
        }

        startInfo.ArgumentList.Add("--stdout");
        startInfo.ArgumentList.Add(request.Text);
        var espeakProcess = StartEspeakPlayback(startInfo);
        return Task.FromResult<ITtsPlaybackSession>(
            espeakProcess is null
                ? InactiveTtsPlaybackSession.Instance
                : new ProcessTtsPlaybackSession(espeakProcess, ct)
        );
    }

    public void Dispose() { }

    private static Process? StartEspeakPlayback(ProcessStartInfo espeakStartInfo)
    {
        // espeak/espeak-ng write PCM audio to stdout with --stdout. If a player
        // (paplay for PipeWire/PulseAudio, aplay for ALSA) is available, pipe
        // into it so we don't rely on espeak's built-in audio output (which
        // requires its own audio library to be present).
        // We use `sh -c '...' sh "$text"` to avoid shell word-splitting on the
        // TTS text while still letting the pipe operator work in the command.
        var player = ResolvePlayer();
        if (player is null)
        {
            return Process.Start(espeakStartInfo);
        }

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
        if (CommandExists("paplay"))
        {
            return "paplay";
        }

        if (CommandExists("aplay"))
        {
            return "aplay";
        }

        return null;
    }

    private static bool CommandExists(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (
            var dir in path.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            if (File.Exists(Path.Combine(dir, name)))
            {
                return true;
            }
        }

        return false;
    }

    private static string Quote(string value)
    {
        return "'" + value.Replace("'", "'\\''") + "'";
    }
}

internal sealed class ProcessTtsPlaybackSession : ITtsPlaybackSession, IDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenRegistration _registration;
    private int _completed;

    public ProcessTtsPlaybackSession(Process process, CancellationToken ct)
    {
        _process = process;
        _process.EnableRaisingEvents = true;
        _process.Exited += OnExited;
        _registration = ct.Register(Stop);

        if (_process.HasExited)
        {
            Finish();
        }
    }

    public void Dispose()
    {
        Stop();
    }

    public bool IsActive => Volatile.Read(ref _completed) == 0 && !_process.HasExited;

    public event EventHandler? Completed;

    public void Stop()
    {
        if (Volatile.Read(ref _completed) != 0)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcessTtsPlaybackSession] stop failed: {ex.Message}");
        }

        Finish();
    }

    private void OnExited(object? sender, EventArgs e)
    {
        Finish();
    }

    private void Finish()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        _process.Exited -= OnExited;
        _registration.Dispose();
        _process.Dispose();
        Completed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
///     Sentinel returned when TTS is not available or the text is empty.
///     Immediately fires <see cref="Completed" /> to any subscriber so callers
///     don't have to special-case a null session.
/// </summary>
internal sealed class InactiveTtsPlaybackSession : ITtsPlaybackSession
{
    private InactiveTtsPlaybackSession() { }
    public static InactiveTtsPlaybackSession Instance { get; } = new();

    public bool IsActive => false;

    public event EventHandler? Completed
    {
        add { value?.Invoke(this, EventArgs.Empty); }
        remove { }
    }

    public void Stop() { }
}