// The counters and flags on these doubles are the assertion surface, not internal state:
// each is written by the boundary being faked and read by whichever test needs that signal.
// Paired members (duck/restore, pause/resume, start/stop) stay symmetric and public so a test
// can assert either side without editing the double, so some have no reader yet.
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable UnusedMember.Global
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using TypeWhisper.Linux.Services.Hotkey;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using TypeWhisper.PluginSDK.Processes;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Integration.Tests.TestDoubles;

internal sealed class RecordingSystemAudio : IAudioDuckingService, IMediaPauseService
{
    public int DuckCount { get; private set; }
    public int RestoreCount { get; private set; }
    public int PauseCount { get; private set; }
    public int ResumeCount { get; private set; }
    public bool IsDucked { get; private set; }
    public bool IsPaused { get; private set; }

    public void DuckAudio(float factor)
    {
        DuckCount++;
        IsDucked = true;
    }

    public void RestoreAudio()
    {
        RestoreCount++;
        IsDucked = false;
    }

    public void PauseMedia()
    {
        PauseCount++;
        IsPaused = true;
    }

    public void ResumeMedia()
    {
        ResumeCount++;
        IsPaused = false;
    }
}

internal sealed class RecordingAudioBoundary
{
    private int _activeStreams;
    private int _maxActiveStreams;

    internal int OpenCount { get; private set; }
    internal int StopCount { get; private set; }
    internal int ActiveStreams => Volatile.Read(ref _activeStreams);
    internal int MaxActiveStreams => Volatile.Read(ref _maxActiveStreams);

    internal AudioRecordingService CreateService(IErrorLogService errorLog)
    {
        return new AudioRecordingService(
            openInputStream: _ =>
            {
                OpenCount++;
                var active = Interlocked.Increment(ref _activeStreams);
                while (true)
                {
                    var observed = Volatile.Read(ref _maxActiveStreams);
                    if (active <= observed
                        || Interlocked.CompareExchange(ref _maxActiveStreams, active, observed)
                            == observed)
                    {
                        break;
                    }
                }
            },
            defaultInputDeviceIndexProvider: static () => 0,
            stopAndDisposeInputStream: () =>
            {
                StopCount++;
                Interlocked.Decrement(ref _activeStreams);
            },
            errorLog: errorLog,
            postToUiThread: static action => action()
        );
    }
}

internal sealed class RecordingPlaybackBoundary
{
    internal int InitializeCount { get; private set; }
    internal int TerminateCount { get; private set; }
    internal int PlayCount { get; private set; }

    internal AudioPlaybackService CreateService()
    {
        return new AudioPlaybackService(
            () => InitializeCount++,
            () => TerminateCount++,
            _ => PlayCount++
        );
    }
}

internal sealed class HeadlessDefaultDeviceWatcher : IDefaultDeviceChangeWatcher
{
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }

    public void Start(Action onDefaultDeviceChanged)
    {
        Started = true;
    }

    public void Stop()
    {
        Stopped = true;
        Started = false;
    }

    public void Dispose()
    {
        Disposed = true;
        Stop();
    }
}

internal sealed class HeadlessSessionActivityMonitor : ISessionActivityMonitor
{
    public bool IsInputAllowed { get; private set; } = true;
    public int InitializeCount { get; private set; }
    public bool Disposed { get; private set; }

    public event EventHandler? InputAllowedChanged;

    public Task InitializeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        InitializeCount++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    internal void SetInputAllowed(bool allowed)
    {
        IsInputAllowed = allowed;
        InputAllowedChanged?.Invoke(this, EventArgs.Empty);
    }
}

#pragma warning disable CS0067 // Interface-required fake events are intentionally never raised.
internal sealed class HeadlessShortcutBackend : IGlobalShortcutBackend
{
    public string Id => "integration-headless";
    public string DisplayName => "Integration headless shortcut backend";
    public bool SupportsPressRelease => true;
    public bool IsGlobalScope => false;
    public bool IsDisposed { get; private set; }

    public bool IsAvailable()
    {
        return true;
    }

    public Task<GlobalShortcutRegistrationResult> RegisterAsync(
        GlobalShortcutSet shortcuts,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(
            new GlobalShortcutRegistrationResult(true, Id, null, false, null)
        );
    }

    public Task UnregisterAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }

    public event EventHandler? DictationToggleRequested;
    public event EventHandler? DictationStartRequested;
    public event EventHandler? DictationStopRequested;
    public event EventHandler? DictationDiscardRequested;
    public event EventHandler? PromptPaletteRequested;
    public event EventHandler? TransformSelectionRequested;
    public event EventHandler? RecentTranscriptionsRequested;
    public event EventHandler? CopyLastTranscriptionRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler<string>? PromptActionRequested;
    public event EventHandler<string>? ProfileDictationToggleRequested;
    public event EventHandler<string>? ProfileDictationStartRequested;
    public event EventHandler? ProfileDictationStopRequested;
    public event EventHandler<string>? ProfileTextProcessingRequested;
    public event EventHandler<string>? Failed;
}

internal sealed class HeadlessAtSpiClient : IAtSpiEventClient
{
    private sealed class Lease : IDisposable
    {
        public void Dispose() { }
    }

    public AtSpiElementRef? CurrentFocusedElement => null;
    public bool IsRunning => false;
    public int StartRequestCount { get; private set; }

    public event Action<AtSpiElementRef>? FocusChanged;
    public event Action<AtSpiElementRef>? TextChanged;

    public IReadOnlyList<AtSpiElementRef> GetRecentFocusedElements()
    {
        return [];
    }

    public Task<bool> EnsureStartedAsync()
    {
        StartRequestCount++;
        return Task.FromResult(false);
    }

    public IDisposable AcquireTextChangedEvents()
    {
        return new Lease();
    }

    public Task StopAsync()
    {
        return Task.CompletedTask;
    }

    public Task<string?> TryReadTextAsync(AtSpiElementRef element, int maxLength)
    {
        return Task.FromResult<string?>(null);
    }

    public Task<bool?> IsPasswordFieldAsync(AtSpiElementRef element)
    {
        return Task.FromResult<bool?>(null);
    }

    public Task<AtSpiScreenRect?> TryGetScreenExtentsAsync(AtSpiElementRef element)
    {
        return Task.FromResult<AtSpiScreenRect?>(null);
    }

    public Task PokeAccessibilityTreesAsync()
    {
        return Task.CompletedTask;
    }

    public Task<AtSpiElementRef?> TryBootstrapFocusAsync()
    {
        return Task.FromResult<AtSpiElementRef?>(null);
    }
}
#pragma warning restore CS0067

internal class HeadlessProcessRunner : IProcessRunner
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _requests = new();
    private int _requestCount;

    internal int RequestCount => Volatile.Read(ref _requestCount);

    // Recorded so a boundary violation names the command that crossed it; a bare count leaves
    // the reader guessing which service shelled out.
    internal IReadOnlyList<string> Requests => _requests.ToArray();

    internal void Reset()
    {
        _requests.Clear();
        Volatile.Write(ref _requestCount, 0);
    }

    private void Record(string what)
    {
        _requests.Enqueue(what);
        Interlocked.Increment(ref _requestCount);
    }

    public virtual Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment = null,
        string? standardInput = null,
        TimeSpan? timeout = null,
        bool detachAfterExit = false,
        CancellationToken ct = default
    )
    {
        ct.ThrowIfCancellationRequested();
        Record($"RunAsync {fileName} {string.Join(' ', args)}".TrimEnd());
        return Task.FromResult(
            new ProcessRunResult(false, false, -1, "", "Blocked by the headless boundary.")
        );
    }

    public ProcessRunOutcome RunProbe(
        ProcessCommand command,
        ProcessOneShotOptions options,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record($"RunProbe {command.FileName} {string.Join(' ', command.Arguments)}".TrimEnd());
        return Failed(command.FileName);
    }

    public Task<ProcessRunOutcome> RunOneShotAsync(
        ProcessCommand command,
        ProcessOneShotOptions options,
        CancellationToken cancellationToken = default
    )
    {
        return Task.FromResult(RunProbe(command, options, cancellationToken));
    }

    public ProcessSessionStartOutcome StartSession(
        ProcessCommand command,
        ProcessSessionOptions options
    )
    {
        Record($"StartSession {command.FileName}");
        return new ProcessSessionStartOutcome(null, $"Blocked {command.FileName}.");
    }

    public DetachedLaunchOutcome LaunchDetached(ProcessCommand command)
    {
        Record($"LaunchDetached {command.FileName}");
        return new DetachedLaunchOutcome(false, $"Blocked {command.FileName}.");
    }

    public DetachedLaunchOutcome LaunchUri(Uri uri)
    {
        Record($"LaunchUri {uri.Scheme}");
        return new DetachedLaunchOutcome(false, $"Blocked {uri.Scheme} URI launch.");
    }

    private static ProcessRunOutcome Failed(string fileName)
    {
        return new ProcessRunOutcome(
            ProcessRunStatus.StartFailed,
            null,
            [],
            [],
            ProcessOutputStatus.Complete,
            $"Blocked {fileName}."
        );
    }
}

internal sealed class GatedCueProcessRunner : HeadlessProcessRunner
{
    private TaskCompletionSource? _cueStarted;
    private TaskCompletionSource? _cueRelease;

    internal void BlockNextStartCue()
    {
        _cueStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cueRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal Task WaitForCueAsync()
    {
        return BoundedTest.WaitAsync(
            _cueStarted?.Task
            ?? Task.FromException(new InvalidOperationException("No cue gate was armed."))
        );
    }

    internal void ReleaseCue()
    {
        _cueRelease?.TrySetResult();
    }

    public override async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment = null,
        string? standardInput = null,
        TimeSpan? timeout = null,
        bool detachAfterExit = false,
        CancellationToken ct = default
    )
    {
        // Captured before the check so an overlapping cue clearing the fields mid-await
        // can't leave this call signalling or waiting on a null gate.
        var started = _cueStarted;
        var release = _cueRelease;

        // ReSharper disable once InvertIf -- inverting duplicates the success return and negates
        // a four-clause condition; the positive form states when the gate applies.
        if (
            args.Count > 0
            && string.Equals(Path.GetFileName(args[0]), "start.wav", StringComparison.Ordinal)
            && started is not null
            && release is not null
        )
        {
            started.TrySetResult();
            await release.Task
                .WaitAsync(BoundedTest.s_innerTimeout, ct)
                .ConfigureAwait(false);
            _cueStarted = null;
            _cueRelease = null;
        }

        return new ProcessRunResult(true, false, 0, "", "");
    }
}

internal sealed class UnconfiguredTtsProvider : ITtsProviderPlugin
{
    public string PluginId => "integration.unconfigured-tts";
    public string PluginName => "Integration unconfigured TTS";
    public string PluginVersion => "1.0.0";
    public string ProviderId => PluginId;
    public string ProviderDisplayName => PluginName;
    public bool IsConfigured => false;
    public IReadOnlyList<PluginVoiceInfo> AvailableVoices => [];
    public string? SelectedVoiceId => null;

    public void SelectVoice(string? voiceId) { }

    public Task<ITtsPlaybackSession> SpeakAsync(TtsSpeakRequest request, CancellationToken ct)
    {
        return Task.FromException<ITtsPlaybackSession>(
            new InvalidOperationException("The integration TTS boundary is unconfigured.")
        );
    }

    public Task ActivateAsync(IPluginHostServices host)
    {
        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        return Task.CompletedTask;
    }

    public void Dispose() { }
}
