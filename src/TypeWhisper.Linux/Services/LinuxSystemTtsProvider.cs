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
    private static readonly TimeSpan s_dispatcherCancellationTimeout = TimeSpan.FromMilliseconds(
        500
    );

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

        var language = NormalizeLanguageHint(request.Language);
        var args = BuildArguments(command, request.Text, language);
        // ReSharper disable once SuggestVarOrType_Elsewhere -- the collection-expression arm has no natural type; `var` would not compile.
        IReadOnlyList<string>? fallbackArgs = language is not null && args.Count > 1
            ? BuildDefaultArguments(command, request.Text)
            : null;
        IReadOnlyList<string>? cancellationArgs = command == "spd-say" ? ["-C"] : null;

        // espeak/espeak-ng and spd-say both own their audio output. Arguments
        // remain separate argv items so no shell or intermediate audio is needed.
        // spd-say waits for END/CANCEL so the session tracks the utterance. Its
        // stock CLI exposes only global CANCEL ALL for discarding both current
        // and queued messages, so cancellation can affect other dispatcher clients.
        // If a backend rejects a requested language/voice with a nonzero exit,
        // the session makes one best-effort default-voice attempt within the same
        // timeout budget. Launch failures, timeouts, and cancellation never retry.
        var session = new TaskBackedTtsPlaybackSession(
            _processRunner,
            command,
            args,
            fallbackArgs,
            cancellationArgs,
            s_dispatcherCancellationTimeout,
            CalculatePlaybackTimeout(request.Text.Length),
            ct
        );
        return Task.FromResult<ITtsPlaybackSession>(session);
    }

    public void Dispose() { }

    private static string? NormalizeLanguageHint(string? language)
    {
        var normalized = language?.Trim();
        return string.IsNullOrEmpty(normalized)
               || string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    private static IReadOnlyList<string> BuildArguments(
        string command,
        string text,
        string? language
    )
    {
        if (language is null)
        {
            return BuildDefaultArguments(command, text);
        }

        return command switch
        {
            "espeak" or "espeak-ng" => ["-v", language, text],
            "spd-say" => ["--wait", "-l", language, text],
            _ => BuildDefaultArguments(command, text)
        };
    }

    private static IReadOnlyList<string> BuildDefaultArguments(string command, string text)
    {
        return command == "spd-say" ? ["--wait", text] : [text];
    }

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
        IReadOnlyList<string>? fallbackArgs,
        IReadOnlyList<string>? cancellationArgs,
        TimeSpan cancellationTimeout,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        _invocationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runnerTask = RunInvocationSequenceAsync(
            processRunner,
            command,
            args,
            fallbackArgs,
            cancellationArgs,
            cancellationTimeout,
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

    private static async Task<ProcessRunResult> RunInvocationSequenceAsync(
        IProcessRunner processRunner,
        string command,
        IReadOnlyList<string> args,
        IReadOnlyList<string>? fallbackArgs,
        IReadOnlyList<string>? cancellationArgs,
        TimeSpan cancellationTimeout,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await RunInvocationAsync(processRunner, command, args, timeout, ct)
                .ConfigureAwait(false);
            if (result.TimedOut)
            {
                await RunCancellationAsync(
                        processRunner,
                        command,
                        cancellationArgs,
                        cancellationTimeout
                    )
                    .ConfigureAwait(false);
                return result;
            }

            if (fallbackArgs is null || !result.Started || result.ExitCode == 0)
            {
                return result;
            }

            ct.ThrowIfCancellationRequested();
            var remainingTimeout = timeout - stopwatch.Elapsed;
            if (remainingTimeout <= TimeSpan.Zero)
            {
                return result;
            }

            var fallbackResult = await RunInvocationAsync(
                    processRunner,
                    command,
                    fallbackArgs,
                    remainingTimeout,
                    ct
                )
                .ConfigureAwait(false);
            if (fallbackResult.TimedOut)
            {
                await RunCancellationAsync(
                        processRunner,
                        command,
                        cancellationArgs,
                        cancellationTimeout
                    )
                    .ConfigureAwait(false);
            }

            return fallbackResult;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await RunCancellationAsync(
                    processRunner,
                    command,
                    cancellationArgs,
                    cancellationTimeout
                )
                .ConfigureAwait(false);
            throw;
        }
    }

    private static async Task RunCancellationAsync(
        IProcessRunner processRunner,
        string command,
        IReadOnlyList<string>? cancellationArgs,
        TimeSpan cancellationTimeout
    )
    {
        if (cancellationArgs is null)
        {
            return;
        }

        try
        {
            var result = await processRunner
                .RunAsync(
                    command,
                    cancellationArgs,
                    timeout: cancellationTimeout,
                    ct: CancellationToken.None
                )
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                return;
            }

            if (result.TimedOut)
            {
                Debug.WriteLine(
                    "[LinuxSystemTtsProvider] Speech Dispatcher cancellation timed out."
                );
            }
            else if (!result.Started)
            {
                Debug.WriteLine(
                    "[LinuxSystemTtsProvider] Speech Dispatcher cancellation did not start."
                );
            }
            else
            {
                Debug.WriteLine(
                    $"[LinuxSystemTtsProvider] Speech Dispatcher cancellation exited with code {result.ExitCode}."
                );
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[LinuxSystemTtsProvider] Speech Dispatcher cancellation failed ({ex.GetType().Name})."
            );
        }
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
