using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

public sealed class LinuxSystemTtsProvider : ITtsProviderPlugin
{
    private const string BuiltInProviderId = AppSettings.DefaultSpokenFeedbackProviderId;
    private const long PlaybackStartupMilliseconds = 5_000;
    private const long PlaybackMillisecondsPerUtf16Character = 200;
    private const long MinimumPlaybackMilliseconds = 15_000;
    private const long MaximumPlaybackMilliseconds = 10 * 60 * 1_000;

    private readonly Func<string?> _speechFeedbackCommand;
    private readonly IProcessRunner _processRunner;
    private readonly ISettingsService _settings;

    public LinuxSystemTtsProvider(
        ISettingsService settings,
        SystemCommandAvailabilityService commands,
        IProcessRunner processRunner
    )
        : this(settings, processRunner, () => commands.SpeechFeedbackCommand)
    {
    }

    internal LinuxSystemTtsProvider(
        ISettingsService settings,
        IProcessRunner processRunner,
        string? speechFeedbackCommand
    )
        : this(settings, processRunner, () => speechFeedbackCommand)
    {
    }

    private LinuxSystemTtsProvider(
        ISettingsService settings,
        IProcessRunner processRunner,
        Func<string?> speechFeedbackCommand
    )
    {
        _settings = settings;
        _processRunner = processRunner;
        _speechFeedbackCommand = speechFeedbackCommand;
    }

    public string PluginId => "com.typewhisper.tts.linux-system";
    public string PluginName => "Linux System Voice";
    public string PluginVersion => "1.0.0";
    public string ProviderId => BuiltInProviderId;
    public string ProviderDisplayName => "Linux system voice";
    public bool IsConfigured => _speechFeedbackCommand() is not null;
    public string? SelectedVoiceId => _settings.Current.SpokenFeedbackVoiceId;
    public string SettingsSummary => SelectedVoiceId ?? "System default voice";

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

        var command = _speechFeedbackCommand();
        if (command is null)
        {
            return Task.FromResult<ITtsPlaybackSession>(InactiveTtsPlaybackSession.Instance);
        }

        ct.ThrowIfCancellationRequested();

        // espeak/espeak-ng and spd-say both own their audio output; passing the
        // text as a single argv avoids a shell and keeps the runner in sole control.
        var session = new TaskBackedTtsPlaybackSession(
            _processRunner,
            command,
            [request.Text],
            CalculatePlaybackTimeout(request.Text.Length),
            ct
        );
        return Task.FromResult<ITtsPlaybackSession>(session);
    }

    public void Dispose() { }

    internal static TimeSpan CalculatePlaybackTimeout(int utf16CharacterCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(utf16CharacterCount);

        var calculatedMilliseconds = PlaybackStartupMilliseconds
                                     // ReSharper disable once RedundantCast -- explicit long cast documents that the per-character scaling stays in the long domain; part of the deliberate overflow-safe timeout arithmetic.
                                     + (long)utf16CharacterCount
                                     * PlaybackMillisecondsPerUtf16Character;
        return TimeSpan.FromMilliseconds(
            Math.Clamp(
                calculatedMilliseconds,
                MinimumPlaybackMilliseconds,
                MaximumPlaybackMilliseconds
            )
        );
    }
}

internal sealed class TaskBackedTtsPlaybackSession : ITtsPlaybackSession, IDisposable
{
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _invocationCts;
    private readonly Task<ProcessRunResult> _runnerTask;
    private EventHandler? _completedHandlers;
    private int _completed;
    private int _resourcesDisposed;
    private int _stopRequested;

    public TaskBackedTtsPlaybackSession(
        IProcessRunner processRunner,
        string command,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        _invocationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runnerTask = RunInvocationAsync(
            processRunner,
            command,
            args,
            timeout,
            _invocationCts.Token
        );
        _ = ObserveRunnerAsync(command);
    }

    public bool IsActive => !_runnerTask.IsCompleted;

    public event EventHandler? Completed
    {
        add
        {
            if (value is null)
            {
                return;
            }

            var alreadyCompleted = false;
            lock (_sync)
            {
                if (_completed != 0)
                {
                    alreadyCompleted = true;
                }
                else
                {
                    _completedHandlers += value;
                }
            }

            if (alreadyCompleted)
            {
                InvokeCompletedHandler(value);
            }
        }
        remove
        {
            lock (_sync)
            {
                _completedHandlers -= value;
            }
        }
    }

    public void Stop()
    {
        if (
            Volatile.Read(ref _completed) != 0
            || Interlocked.Exchange(ref _stopRequested, 1) != 0
        )
        {
            return;
        }

        try
        {
            _invocationCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Runner completion won the race and already released the source.
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private static async Task<ProcessRunResult> RunInvocationAsync(
        IProcessRunner processRunner,
        string command,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        // Turns a synchronous launch exception into a faulted task instead of a
        // throw from the constructor.
        return await processRunner
            .RunAsync(command, args, timeout: timeout, ct: ct)
            .ConfigureAwait(false);
    }

    private async Task ObserveRunnerAsync(string command)
    {
        try
        {
            var result = await _runnerTask.ConfigureAwait(false);
            if (result.Succeeded)
            {
                return;
            }

            if (result.TimedOut)
            {
                Debug.WriteLine($"[LinuxSystemTtsProvider] {command} playback timed out.");
            }
            else if (!result.Started)
            {
                Debug.WriteLine($"[LinuxSystemTtsProvider] {command} playback did not start.");
            }
            else
            {
                Debug.WriteLine(
                    $"[LinuxSystemTtsProvider] {command} playback exited with code {result.ExitCode}."
                );
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[LinuxSystemTtsProvider] {command} playback was canceled.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[LinuxSystemTtsProvider] {command} playback failed ({ex.GetType().Name})."
            );
        }
        finally
        {
            Finish();
        }
    }

    private void Finish()
    {
        EventHandler? handlers;
        lock (_sync)
        {
            if (_completed != 0)
            {
                return;
            }

            Volatile.Write(ref _completed, 1);
            handlers = _completedHandlers;
            _completedHandlers = null;
        }

        if (Interlocked.Exchange(ref _resourcesDisposed, 1) == 0)
        {
            _invocationCts.Dispose();
        }

        if (handlers is null)
        {
            return;
        }

        // ReSharper disable once PossibleInvalidCastExceptionInForeachLoop -- handlers is an EventHandler-typed multicast delegate, so its invocation list contains only EventHandler instances; the cast cannot fail.
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            InvokeCompletedHandler(handler);
        }
    }

    private void InvokeCompletedHandler(EventHandler handler)
    {
        try
        {
            handler(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[LinuxSystemTtsProvider] playback completion handler failed ({ex.GetType().Name})."
            );
        }
    }
}

/// <summary>
///     Sentinel returned when TTS is unavailable or text is empty. Fires
///     <see cref="Completed" /> immediately so callers need no null check.
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
