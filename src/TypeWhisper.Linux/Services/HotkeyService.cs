using System.Diagnostics;
using SharpHook.Native;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Hotkey;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// Coordinator for global hotkeys. Owns the configured-binding state (the
/// eight shortcuts plus mode), parses user-supplied hotkey strings, and
/// resolves an <see cref="IGlobalShortcutBackend"/> at <see cref="Initialize"/>
/// time. The backend handles actual key-event delivery and raises the typed
/// events that this coordinator re-raises to the rest of the app.
///
/// Three modes, matching the Windows shell:
///   - Toggle: press the hotkey to start recording, press again to stop.
///   - PushToTalk: hold the hotkey to record, release to stop.
///   - Hybrid: starts immediately on press. A short press stays active like
///     Toggle; holding past a threshold (600 ms) stops on release.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private enum HotkeyBinding
    {
        Dictation,
        PromptPalette,
        RecentTranscriptions,
        CopyLastTranscription,
        TransformSelection
    }

    private readonly BackendSelector _selector;
    private readonly object _lock = new();

    private KeyCode _key = KeyCode.VcSpace;
    private ModifierMask _modifiers = ModifierMask.LeftCtrl | ModifierMask.LeftShift;
    private KeyCode? _promptPaletteKey;
    private ModifierMask _promptPaletteModifiers = ModifierMask.None;
    private KeyCode? _recentTranscriptionsKey;
    private ModifierMask _recentTranscriptionsModifiers = ModifierMask.None;
    private KeyCode? _copyLastTranscriptionKey;
    private ModifierMask _copyLastTranscriptionModifiers = ModifierMask.None;
    private KeyCode? _transformSelectionKey;
    private ModifierMask _transformSelectionModifiers = ModifierMask.None;
    private readonly KeyCode _cancelKey = KeyCode.VcEscape;
    private readonly ModifierMask _cancelModifiers = ModifierMask.None;
    private RecordingMode _mode = RecordingMode.Toggle;
    private volatile bool _cancelShortcutEnabled;

    private IGlobalShortcutBackend? _backend;
    // Serializes backend updates so a burst of TrySet*/Mode= calls can't apply
    // out of order and leave the backend listening for stale bindings.
    private Task _pendingBackendUpdate = Task.CompletedTask;
    // Last registration result observed from the backend. Used so callers can
    // discover that the active backend can't deliver release events (i.e.
    // portal/CLI fallbacks) and adjust the UI mode picker accordingly.
    private volatile bool _backendRequiresToggleMode;
    private int _disposed;

    public event EventHandler? DictationToggleRequested;
    public event EventHandler? DictationStartRequested;
    public event EventHandler? DictationStopRequested;
    public event EventHandler? PromptPaletteRequested;
    public event EventHandler? RecentTranscriptionsRequested;
    public event EventHandler? CopyLastTranscriptionRequested;
    public event EventHandler? TransformSelectionRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler<string>? HookFailed;

    public HotkeyService() : this(new BackendSelector()) { }

    public HotkeyService(BackendSelector selector)
    {
        _selector = selector;
    }

    /// <summary>
    /// True when the active backend can't deliver release events (portal or
    /// CLI-only). The coordinator preserves the user's chosen <see cref="Mode"/>
    /// but downstream UI may surface a hint that only Toggle is effective.
    /// </summary>
    public bool BackendRequiresToggleMode => _backendRequiresToggleMode;

    /// <summary>
    /// Stable identifier of the currently active backend (e.g.
    /// "linux-sharphook", "linux-evdev", "linux-xdg-portal"). Null until
    /// <see cref="Initialize"/> has resolved a backend.
    /// </summary>
    public string? ActiveBackendId => _backend?.Id;

    /// <summary>Human-readable name of the active backend, e.g. "Linux evdev".</summary>
    public string? ActiveBackendDisplayName => _backend?.DisplayName;

    /// <summary>
    /// True if the active backend can deliver both press and release events
    /// (so PushToTalk and Hybrid modes are functional). Null while no
    /// backend is resolved.
    /// </summary>
    public bool? ActiveBackendSupportsPressRelease => _backend?.SupportsPressRelease;

    /// <summary>
    /// True if the active backend captures shortcuts regardless of which
    /// window owns focus. Null while no backend is resolved.
    /// </summary>
    public bool? ActiveBackendIsGlobalScope => _backend?.IsGlobalScope;

    /// <summary>
    /// Disposes the current backend and asks the selector to resolve a
    /// fresh one — used when a setting that influences backend selection
    /// flips at runtime (e.g. the Wayland evdev opt-out toggle). Without
    /// this hot-swap path, flipping that toggle would only take effect on
    /// the next app launch, leaving the user's keyboard reads active in
    /// the interim.
    /// </summary>
    public async Task SwitchBackendAsync(CancellationToken ct = default)
    {
        IGlobalShortcutBackend? previous;
        lock (_lock)
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            previous = _backend;
            _backend = null;
            _backendRequiresToggleMode = false;
        }

        if (previous is not null)
        {
            try { await previous.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Trace.WriteLine($"[HotkeyService] Dispose previous backend threw: {ex.Message}"); }
        }

        Initialize();
        OnPropertyChangedHook();
    }

    /// <summary>
    /// Hook for derived/wrapping logic (e.g. unit-test instrumentation) to
    /// observe a backend switch. The base implementation is a no-op.
    /// </summary>
    private static void OnPropertyChangedHook() { }

    /// <summary>
    /// Gates the Escape cancel shortcut. Only true while a dictation is active
    /// (recording or transcription in flight) — outside that window Escape
    /// passes through to the foreground app so we don't shadow modal dialogs,
    /// vim, etc.
    /// </summary>
    public bool IsCancelShortcutEnabled
    {
        get => _cancelShortcutEnabled;
        set
        {
            _cancelShortcutEnabled = value;
            PushShortcutsIfRunning();
        }
    }

    public RecordingMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            PushShortcutsIfRunning();
        }
    }

    public void Initialize()
    {
        lock (_lock)
        {
            if (_backend is not null || Volatile.Read(ref _disposed) == 1) return;
            var backend = _selector.Resolve();
            backend.DictationToggleRequested += (_, _) => DictationToggleRequested?.Invoke(this, EventArgs.Empty);
            backend.DictationStartRequested += (_, _) => DictationStartRequested?.Invoke(this, EventArgs.Empty);
            backend.DictationStopRequested += (_, _) => DictationStopRequested?.Invoke(this, EventArgs.Empty);
            backend.PromptPaletteRequested += (_, _) => PromptPaletteRequested?.Invoke(this, EventArgs.Empty);
            backend.RecentTranscriptionsRequested += (_, _) => RecentTranscriptionsRequested?.Invoke(this, EventArgs.Empty);
            backend.CopyLastTranscriptionRequested += (_, _) => CopyLastTranscriptionRequested?.Invoke(this, EventArgs.Empty);
            backend.TransformSelectionRequested += (_, _) => TransformSelectionRequested?.Invoke(this, EventArgs.Empty);
            backend.CancelRequested += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
            backend.Failed += (_, message) => HookFailed?.Invoke(this, message);
            _backend = backend;
        }

        PushShortcutsIfRunning();
    }

    public void SetHotkey(KeyCode key, ModifierMask modifiers)
    {
        // Defense in depth: TrySet* is the normal path (already rejects
        // collisions), but the raw setter is reachable from tests and any
        // future direct caller. Silently no-op rather than throw so call
        // sites don't need try/catch.
        if (HotkeyMatchesAny(key, modifiers, GetBoundHotkeys(HotkeyBinding.Dictation)))
        {
            Trace.WriteLine("[HotkeyService] Refusing dictation hotkey that collides with another shortcut.");
            return;
        }

        _key = key;
        _modifiers = modifiers;
        PushShortcutsIfRunning();
    }

    public void SetPromptPaletteHotkey(KeyCode? key, ModifierMask modifiers)
    {
        if (key is not null && HotkeyMatchesAny(key.Value, modifiers, GetBoundHotkeys(HotkeyBinding.PromptPalette)))
        {
            Trace.WriteLine("[HotkeyService] Refusing prompt palette hotkey that collides with another shortcut.");
            return;
        }

        _promptPaletteKey = key;
        _promptPaletteModifiers = key is null ? ModifierMask.None : modifiers;
        PushShortcutsIfRunning();
    }

    /// <summary>Human-friendly form of the currently-bound hotkey, e.g. "Ctrl+Shift+Space".</summary>
    public string CurrentHotkeyString => FormatHotkey(_key, _modifiers);
    public string CurrentPromptPaletteHotkeyString =>
        _promptPaletteKey is null ? "" : FormatHotkey(_promptPaletteKey.Value, _promptPaletteModifiers);
    public string CurrentRecentTranscriptionsHotkeyString =>
        _recentTranscriptionsKey is null ? "" : FormatHotkey(_recentTranscriptionsKey.Value, _recentTranscriptionsModifiers);
    public string CurrentCopyLastTranscriptionHotkeyString =>
        _copyLastTranscriptionKey is null ? "" : FormatHotkey(_copyLastTranscriptionKey.Value, _copyLastTranscriptionModifiers);
    public string CurrentTransformSelectionHotkeyString =>
        _transformSelectionKey is null ? "" : FormatHotkey(_transformSelectionKey.Value, _transformSelectionModifiers);

    /// <summary>
    /// Parses strings like "Ctrl+Shift+Space", "Alt+F9", "Ctrl+K" and binds
    /// them. Returns true on success. Accepts modifier tokens (Ctrl, Shift,
    /// Alt, Meta/Win/Super) and either a single letter, a digit, a function
    /// key (F1-F24), or a named key (Space, Enter, Tab, Escape, arrows, etc.).
    /// Invalid input leaves the current binding unchanged.
    /// </summary>
    public bool TrySetHotkeyFromString(string text)
    {
        if (!TryParseHotkey(text, out var key, out var modifiers))
            return false;

        // Don't let the dictation hotkey collide with another configured
        // binding — the matcher orders cancel/palette/etc. ahead of dictation
        // so a collision would shadow this key.
        if (HotkeyMatchesAny(key!.Value, modifiers, GetBoundHotkeys(HotkeyBinding.Dictation)))
            return false;

        SetHotkey(key.Value, modifiers);
        return true;
    }

    public bool TrySetPromptPaletteHotkeyFromString(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            SetPromptPaletteHotkey(null, ModifierMask.None);
            return true;
        }

        if (!TryParseHotkey(text, out var key, out var modifiers))
            return false;

        if (HotkeyMatchesAny(key!.Value, modifiers, GetBoundHotkeys(HotkeyBinding.PromptPalette)))
            return false;

        SetPromptPaletteHotkey(key, modifiers);
        return true;
    }

    public bool TrySetRecentTranscriptionsHotkeyFromString(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _recentTranscriptionsKey = null;
            _recentTranscriptionsModifiers = ModifierMask.None;
            PushShortcutsIfRunning();
            return true;
        }

        if (!TryParseHotkey(text, out var key, out var modifiers))
            return false;

        if (HotkeyMatchesAny(key!.Value, modifiers, GetBoundHotkeys(HotkeyBinding.RecentTranscriptions)))
            return false;

        _recentTranscriptionsKey = key;
        _recentTranscriptionsModifiers = modifiers;
        PushShortcutsIfRunning();
        return true;
    }

    public bool TrySetCopyLastTranscriptionHotkeyFromString(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _copyLastTranscriptionKey = null;
            _copyLastTranscriptionModifiers = ModifierMask.None;
            PushShortcutsIfRunning();
            return true;
        }

        if (!TryParseHotkey(text, out var key, out var modifiers))
            return false;

        if (HotkeyMatchesAny(key!.Value, modifiers, GetBoundHotkeys(HotkeyBinding.CopyLastTranscription)))
            return false;

        _copyLastTranscriptionKey = key;
        _copyLastTranscriptionModifiers = modifiers;
        PushShortcutsIfRunning();
        return true;
    }

    public bool TrySetTransformSelectionHotkeyFromString(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _transformSelectionKey = null;
            _transformSelectionModifiers = ModifierMask.None;
            PushShortcutsIfRunning();
            return true;
        }

        if (!TryParseHotkey(text, out var key, out var modifiers))
            return false;

        if (HotkeyMatchesAny(key!.Value, modifiers, GetBoundHotkeys(HotkeyBinding.TransformSelection)))
            return false;

        _transformSelectionKey = key;
        _transformSelectionModifiers = modifiers;
        PushShortcutsIfRunning();
        return true;
    }

    private GlobalShortcutSet BuildShortcutSet() => new(
        DictationKey: _key,
        DictationModifiers: _modifiers,
        PromptPaletteKey: _promptPaletteKey,
        PromptPaletteModifiers: _promptPaletteModifiers,
        RecentTranscriptionsKey: _recentTranscriptionsKey,
        RecentTranscriptionsModifiers: _recentTranscriptionsModifiers,
        CopyLastTranscriptionKey: _copyLastTranscriptionKey,
        CopyLastTranscriptionModifiers: _copyLastTranscriptionModifiers,
        TransformSelectionKey: _transformSelectionKey,
        TransformSelectionModifiers: _transformSelectionModifiers,
        CancelKey: _cancelKey,
        CancelModifiers: _cancelModifiers,
        Mode: _mode,
        IsCancelEnabled: _cancelShortcutEnabled);

    private void PushShortcutsIfRunning()
    {
        IGlobalShortcutBackend? backend;
        GlobalShortcutSet snapshot;
        lock (_lock)
        {
            backend = _backend;
            if (backend is null) return;
            snapshot = BuildShortcutSet();
            // Chain on the previous registration so a burst of changes applies
            // in order. Each link observes the result and surfaces failures
            // through HookFailed; the chain itself never throws because every
            // exception is caught inside the continuation.
            _pendingBackendUpdate = _pendingBackendUpdate.ContinueWith(
                async _ =>
                {
                    try
                    {
                        var result = await backend.RegisterAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
                        _backendRequiresToggleMode = result.RequiresToggleMode;
                        if (!result.Success)
                        {
                            var message = result.UserMessage
                                ?? $"Backend '{result.BackendId}' rejected the shortcut registration.";
                            HookFailed?.Invoke(this, message);
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[HotkeyService] Backend registration threw: {ex.Message}");
                        HookFailed?.Invoke(this, ex.Message);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).Unwrap();
        }
    }

    private static bool HotkeyMatches(KeyCode key, ModifierMask modifiers, KeyCode? otherKey, ModifierMask otherModifiers)
    {
        if (otherKey is null) return false;
        return key == otherKey.Value && modifiers == otherModifiers;
    }

    private IEnumerable<(KeyCode? Key, ModifierMask Modifiers)> GetBoundHotkeys(HotkeyBinding? exclude = null)
    {
        if (exclude != HotkeyBinding.Dictation)
            yield return (_key, _modifiers);
        if (exclude != HotkeyBinding.PromptPalette)
            yield return (_promptPaletteKey, _promptPaletteModifiers);
        if (exclude != HotkeyBinding.RecentTranscriptions)
            yield return (_recentTranscriptionsKey, _recentTranscriptionsModifiers);
        if (exclude != HotkeyBinding.CopyLastTranscription)
            yield return (_copyLastTranscriptionKey, _copyLastTranscriptionModifiers);
        if (exclude != HotkeyBinding.TransformSelection)
            yield return (_transformSelectionKey, _transformSelectionModifiers);
    }

    private static bool HotkeyMatchesAny(KeyCode key, ModifierMask modifiers, IEnumerable<(KeyCode? Key, ModifierMask Modifiers)> others) =>
        others.Any(other => HotkeyMatches(key, modifiers, other.Key, other.Modifiers));

    private static string FormatHotkey(KeyCode key, ModifierMask mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierMask.LeftCtrl) || mods.HasFlag(ModifierMask.RightCtrl)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierMask.LeftShift) || mods.HasFlag(ModifierMask.RightShift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierMask.LeftAlt) || mods.HasFlag(ModifierMask.RightAlt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierMask.LeftMeta) || mods.HasFlag(ModifierMask.RightMeta)) parts.Add("Meta");

        var keyName = key.ToString();
        if (keyName.StartsWith("Vc", StringComparison.Ordinal)) keyName = keyName[2..];
        parts.Add(keyName);
        return string.Join('+', parts);
    }

    private static bool TryParseHotkey(string text, out KeyCode? key, out ModifierMask modifiers)
    {
        key = null;
        modifiers = ModifierMask.None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        foreach (var raw in parts)
        {
            var part = raw.ToLowerInvariant();
            switch (part)
            {
                case "ctrl" or "control": modifiers |= ModifierMask.LeftCtrl; continue;
                case "shift": modifiers |= ModifierMask.LeftShift; continue;
                case "alt": modifiers |= ModifierMask.LeftAlt; continue;
                case "meta" or "super" or "win": modifiers |= ModifierMask.LeftMeta; continue;
            }

            if (part.Length == 1 && part[0] is >= 'a' and <= 'z')
            {
                if (key is not null) return false;
                key = (KeyCode)Enum.Parse(typeof(KeyCode), $"Vc{char.ToUpperInvariant(part[0])}");
                continue;
            }

            if (part.Length == 1 && part[0] is >= '0' and <= '9')
            {
                if (key is not null) return false;
                key = (KeyCode)Enum.Parse(typeof(KeyCode), $"Vc{part[0]}");
                continue;
            }

            var named = part switch
            {
                "space" => KeyCode.VcSpace,
                "enter" or "return" => KeyCode.VcEnter,
                "tab" => KeyCode.VcTab,
                "escape" or "esc" => KeyCode.VcEscape,
                "backspace" => KeyCode.VcBackspace,
                "delete" or "del" => KeyCode.VcDelete,
                "home" => KeyCode.VcHome,
                "end" => KeyCode.VcEnd,
                "pageup" => KeyCode.VcPageUp,
                "pagedown" => KeyCode.VcPageDown,
                "left" => KeyCode.VcLeft,
                "right" => KeyCode.VcRight,
                "up" => KeyCode.VcUp,
                "down" => KeyCode.VcDown,
                _ => (KeyCode?)null,
            };
            if (named is not null)
            {
                if (key is not null) return false;
                key = named.Value;
                continue;
            }

            if (part.Length is >= 2 and <= 3 && part[0] == 'f' &&
                int.TryParse(part[1..], out var fNum) && fNum is >= 1 and <= 24)
            {
                if (key is not null) return false;
                key = (KeyCode)Enum.Parse(typeof(KeyCode), $"VcF{fNum}");
                continue;
            }

            key = null;
            return false;
        }

        return key is not null;
    }

    /// <summary>
    /// Compatibility shim for callers (notably tests) — forwards to
    /// <see cref="ShortcutMatcher.ModifiersMatch"/>.
    /// </summary>
    internal static bool ModifiersMatch(ModifierMask pressed, ModifierMask required) =>
        ShortcutMatcher.ModifiersMatch(pressed, required);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        IGlobalShortcutBackend? backend;
        lock (_lock)
        {
            backend = _backend;
            _backend = null;
        }

        if (backend is null) return;

        var disposeTask = Task.Run(async () =>
        {
            try { await backend.DisposeAsync(); }
            catch (Exception ex) { Trace.WriteLine($"[HotkeyService] Backend dispose threw: {ex.Message}"); }
        });
        disposeTask.Wait(TimeSpan.FromSeconds(1));
    }
}
