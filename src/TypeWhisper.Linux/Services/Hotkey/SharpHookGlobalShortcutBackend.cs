using System.Diagnostics;
using SharpHook;
using SharpHook.Native;

namespace TypeWhisper.Linux.Services.Hotkey;

/// <summary>
/// SharpHook-backed implementation. Works reliably on X11; on Wayland the
/// hook only receives events while the application owns focus — that's the
/// gap Phase 2's evdev backend closes.
///
/// Hands off the configured-chord state machine to a shared
/// <see cref="ShortcutDispatcher"/> so user-visible press/release/mode
/// behavior stays identical across SharpHook and evdev.
/// </summary>
public sealed class SharpHookGlobalShortcutBackend : IGlobalShortcutBackend
{
    public const string BackendId = "linux-sharphook";

    private readonly TaskPoolGlobalHook _hook = new();
    private readonly ShortcutDispatcher _dispatcher = new();
    private readonly object _lock = new();

    private bool _running;
    private int _disposed;
    private Task? _hookTask;

    public string Id => BackendId;
    public string DisplayName => "SharpHook (libuiohook)";
    public bool SupportsPressRelease => true;
    public bool IsAvailable() => true;

    public event EventHandler? DictationToggleRequested;
    public event EventHandler? DictationStartRequested;
    public event EventHandler? DictationStopRequested;
    public event EventHandler? PromptPaletteRequested;
    public event EventHandler? TransformSelectionRequested;
    public event EventHandler? RecentTranscriptionsRequested;
    public event EventHandler? CopyLastTranscriptionRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler<string>? Failed;

    public SharpHookGlobalShortcutBackend()
    {
        _dispatcher.DictationToggleRequested += () => DictationToggleRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.DictationStartRequested += () => DictationStartRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.DictationStopRequested += () => DictationStopRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.PromptPaletteRequested += () => PromptPaletteRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.TransformSelectionRequested += () => TransformSelectionRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.RecentTranscriptionsRequested += () => RecentTranscriptionsRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.CopyLastTranscriptionRequested += () => CopyLastTranscriptionRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.CancelRequested += () => CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    public Task<GlobalShortcutRegistrationResult> RegisterAsync(GlobalShortcutSet shortcuts, CancellationToken ct)
    {
        _dispatcher.UpdateShortcuts(shortcuts);

        lock (_lock)
        {
            if (!_running && Volatile.Read(ref _disposed) == 0)
            {
                _hook.KeyPressed += OnKeyPressed;
                _hook.KeyReleased += OnKeyReleased;
                _hookTask = _hook.RunAsync();
                _hookTask.ContinueWith(task =>
                {
                    if (Volatile.Read(ref _disposed) == 1 || task.IsCanceled)
                        return;

                    var error = task.Exception?.GetBaseException().Message
                        ?? "Global hotkey hook stopped unexpectedly.";
                    Trace.WriteLine($"[SharpHookBackend] Hook failed: {error}");
                    Failed?.Invoke(this, error);
                }, TaskContinuationOptions.NotOnRanToCompletion);
                _running = true;
            }
        }

        return Task.FromResult(new GlobalShortcutRegistrationResult(
            Success: true,
            BackendId: BackendId,
            UserMessage: null,
            RequiresToggleMode: false,
            TroubleshootingCommand: null));
    }

    public Task UnregisterAsync(CancellationToken ct)
    {
        _dispatcher.ClearShortcuts();
        return Task.CompletedTask;
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e) =>
        _dispatcher.Handle(e.Data.KeyCode, e.RawEvent.Mask, pressed: true);

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e) =>
        _dispatcher.Handle(e.Data.KeyCode, e.RawEvent.Mask, pressed: false);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return ValueTask.CompletedTask;
        lock (_lock)
        {
            _hook.KeyPressed -= OnKeyPressed;
            _hook.KeyReleased -= OnKeyReleased;
            _running = false;
        }

        var disposeTask = Task.Run(() =>
        {
            try { _hook.Dispose(); }
            catch (Exception ex) { Trace.WriteLine($"[SharpHookBackend] Dispose threw: {ex.Message}"); }
        });
        disposeTask.Wait(TimeSpan.FromSeconds(1));
        return ValueTask.CompletedTask;
    }
}
