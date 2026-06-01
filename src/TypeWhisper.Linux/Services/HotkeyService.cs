using SharpHook.Native;
using System.Diagnostics;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Hotkey;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Coordinator for global hotkeys. Owns the configured-binding state (the
///     eight shortcuts plus mode), parses user-supplied hotkey strings, and
///     resolves an <see cref="IGlobalShortcutBackend" /> at <see cref="Initialize" />
///     time. The backend handles actual key-event delivery and raises the typed
///     events that this coordinator re-raises to the rest of the app.
///     Three modes, matching the Windows shell:
///     - Toggle: press the hotkey to start recording, press again to stop.
///     - PushToTalk: hold the hotkey to record, release to stop.
///     - Hybrid: starts immediately on press. A short press stays active like
///     Toggle; holding past a threshold (600 ms) stops on release.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly KeyCode _cancelKey = KeyCode.VcEscape;
    private readonly ModifierMask _cancelModifiers = ModifierMask.None;
    private readonly object _lock = new();

    private readonly BackendSelector _selector;

    private IGlobalShortcutBackend? _backend;

    // Last registration result observed from the backend. Used so callers can
    // discover that the active backend can't deliver release events (i.e.
    // portal/CLI fallbacks) and adjust the UI mode picker accordingly.
    private volatile bool _backendRequiresToggleMode;
    private volatile bool _cancelShortcutEnabled;
    private KeyCode? _copyLastTranscriptionKey;
    private ModifierMask _copyLastTranscriptionModifiers = ModifierMask.None;
    private int _disposed;

    private KeyCode _key = KeyCode.VcSpace;
    private RecordingMode _mode = RecordingMode.Toggle;
    private ModifierMask _modifiers = ModifierMask.LeftCtrl | ModifierMask.LeftShift;
    private EventHandler<string>? _onBackendFailed;
    private EventHandler? _onCancelRequested;
    private EventHandler? _onCopyLastTranscriptionRequested;
    private EventHandler? _onDictationStartRequested;
    private EventHandler? _onDictationStopRequested;
    private EventHandler? _onDictationToggleRequested;
    private EventHandler? _onPromptPaletteRequested;
    private EventHandler<string>? _onPromptActionRequested;
    private EventHandler<string>? _onProfileDictationToggleRequested;
    private EventHandler<string>? _onProfileDictationStartRequested;
    private EventHandler? _onProfileDictationStopRequested;
    private EventHandler<string>? _onProfileTextProcessingRequested;
    private EventHandler? _onRecentTranscriptionsRequested;
    private EventHandler? _onTransformSelectionRequested;

    // Direct-execution prompt action hotkeys (B12). The list is rebuilt
    // wholesale by SetPromptActionHotkeys; the snapshot pushed to the
    // backend captures it by reference, so mutations after push are not
    // observed by the running matcher.
    private IReadOnlyList<PromptActionHotkey> _promptActionHotkeys =
        Array.Empty<PromptActionHotkey>();

    // Per-profile hotkeys (one chord per profile). Rebuilt wholesale by
    // SetProfileHotkeys; captured by reference in the snapshot exactly like
    // _promptActionHotkeys above.
    private IReadOnlyList<ProfileHotkey> _profileHotkeys = Array.Empty<ProfileHotkey>();

    // Serializes backend updates so a burst of TrySet*/Mode= calls can't apply
    // out of order and leave the backend listening for stale bindings.
    private Task _pendingBackendUpdate = Task.CompletedTask;
    private KeyCode? _promptPaletteKey;
    private ModifierMask _promptPaletteModifiers = ModifierMask.None;
    private KeyCode? _recentTranscriptionsKey;
    private ModifierMask _recentTranscriptionsModifiers = ModifierMask.None;
    private KeyCode? _transformSelectionKey;
    private ModifierMask _transformSelectionModifiers = ModifierMask.None;

    public HotkeyService()
        : this(new BackendSelector())
    {
    }

    public HotkeyService(BackendSelector selector)
    {
        _selector = selector;
    }

    /// <summary>
    ///     True when the active backend can't deliver release events (portal or
    ///     CLI-only). The coordinator preserves the user's chosen <see cref="Mode" />
    ///     but downstream UI may surface a hint that only Toggle is effective.
    /// </summary>
    public bool BackendRequiresToggleMode => _backendRequiresToggleMode;

    /// <summary>
    ///     Stable identifier of the currently active backend (e.g.
    ///     "linux-sharphook", "linux-evdev", "linux-xdg-portal"). Null until
    ///     <see cref="Initialize" /> has resolved a backend.
    /// </summary>
    public string? ActiveBackendId => _backend?.Id;

    /// <summary>Human-readable name of the active backend, e.g. "Linux evdev".</summary>
    public string? ActiveBackendDisplayName => _backend?.DisplayName;

    /// <summary>
    ///     True if the active backend can deliver both press and release events
    ///     (so PushToTalk and Hybrid modes are functional). Null while no
    ///     backend is resolved.
    /// </summary>
    public bool? ActiveBackendSupportsPressRelease => _backend?.SupportsPressRelease;

    /// <summary>
    ///     True if the active backend captures shortcuts regardless of which
    ///     window owns focus. Null while no backend is resolved.
    /// </summary>
    public bool? ActiveBackendIsGlobalScope => _backend?.IsGlobalScope;

    /// <summary>
    ///     Gates the Escape cancel shortcut. Only true while a dictation is active
    ///     (recording or transcription in flight) — outside that window Escape
    ///     passes through to the foreground app so we don't shadow modal dialogs,
    ///     vim, etc.
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

    /// <summary>Human-friendly form of the currently-bound hotkey, e.g. "Ctrl+Shift+Space".</summary>
    public string CurrentHotkeyString => FormatHotkey(_key, _modifiers);

    public string CurrentPromptPaletteHotkeyString =>
        _promptPaletteKey is null
            ? ""
            : FormatHotkey(_promptPaletteKey.Value, _promptPaletteModifiers);

    public string CurrentRecentTranscriptionsHotkeyString =>
        _recentTranscriptionsKey is null
            ? ""
            : FormatHotkey(_recentTranscriptionsKey.Value, _recentTranscriptionsModifiers);

    public string CurrentCopyLastTranscriptionHotkeyString =>
        _copyLastTranscriptionKey is null
            ? ""
            : FormatHotkey(_copyLastTranscriptionKey.Value, _copyLastTranscriptionModifiers);

    public string CurrentTransformSelectionHotkeyString =>
        _transformSelectionKey is null
            ? ""
            : FormatHotkey(_transformSelectionKey.Value, _transformSelectionModifiers);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        IGlobalShortcutBackend? backend;
        lock (_lock)
        {
            backend = _backend;
            _backend = null;
            UnsubscribeBackendHandlers(backend);
        }

        if (backend is null)
        {
            return;
        }

        var disposeTask = Task.Run(async () =>
        {
            try
            {
                await backend.DisposeAsync();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[HotkeyService] Backend dispose threw: {ex.Message}");
            }
        });
        disposeTask.Wait(TimeSpan.FromSeconds(1));
    }

    /// <summary>
    ///     Disposes the current backend and asks the selector to resolve a
    ///     fresh one — used when a setting that influences backend selection
    ///     flips at runtime (e.g. the Wayland evdev opt-out toggle). Without
    ///     this hot-swap path, flipping that toggle would only take effect on
    ///     the next app launch, leaving the user's keyboard reads active in
    ///     the interim.
    /// </summary>
    public async Task SwitchBackendAsync(CancellationToken ct = default)
    {
        IGlobalShortcutBackend? previous;
        lock (_lock)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            previous = _backend;
            _backend = null;
            _backendRequiresToggleMode = false;
            UnsubscribeBackendHandlers(previous);
        }

        if (previous is not null)
        {
            try
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[HotkeyService] Dispose previous backend threw: {ex.Message}");
            }
        }

        Initialize();
        OnPropertyChangedHook();
    }

    public void Initialize()
    {
        lock (_lock)
        {
            if (_backend is not null || Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            var backend = _selector.Resolve();
            _onDictationToggleRequested = (_, _) =>
                DictationToggleRequested?.Invoke(this, EventArgs.Empty);
            _onDictationStartRequested = (_, _) =>
                DictationStartRequested?.Invoke(this, EventArgs.Empty);
            _onDictationStopRequested = (_, _) =>
                DictationStopRequested?.Invoke(this, EventArgs.Empty);
            _onPromptPaletteRequested = (_, _) =>
                PromptPaletteRequested?.Invoke(this, EventArgs.Empty);
            _onRecentTranscriptionsRequested = (_, _) =>
                RecentTranscriptionsRequested?.Invoke(this, EventArgs.Empty);
            _onCopyLastTranscriptionRequested = (_, _) =>
                CopyLastTranscriptionRequested?.Invoke(this, EventArgs.Empty);
            _onTransformSelectionRequested = (_, _) =>
                TransformSelectionRequested?.Invoke(this, EventArgs.Empty);
            _onCancelRequested = (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
            _onPromptActionRequested = (_, actionId) =>
                PromptActionHotkeyTriggered?.Invoke(this, actionId);
            _onProfileDictationToggleRequested = (_, profileId) =>
                ProfileDictationToggleRequested?.Invoke(this, profileId);
            _onProfileDictationStartRequested = (_, profileId) =>
                ProfileDictationStartRequested?.Invoke(this, profileId);
            _onProfileDictationStopRequested = (_, _) =>
                ProfileDictationStopRequested?.Invoke(this, EventArgs.Empty);
            _onProfileTextProcessingRequested = (_, profileId) =>
                ProfileTextProcessingRequested?.Invoke(this, profileId);
            _onBackendFailed = (_, message) => HookFailed?.Invoke(this, message);
            backend.DictationToggleRequested += _onDictationToggleRequested;
            backend.DictationStartRequested += _onDictationStartRequested;
            backend.DictationStopRequested += _onDictationStopRequested;
            backend.PromptPaletteRequested += _onPromptPaletteRequested;
            backend.RecentTranscriptionsRequested += _onRecentTranscriptionsRequested;
            backend.CopyLastTranscriptionRequested += _onCopyLastTranscriptionRequested;
            backend.TransformSelectionRequested += _onTransformSelectionRequested;
            backend.CancelRequested += _onCancelRequested;
            backend.PromptActionRequested += _onPromptActionRequested;
            backend.ProfileDictationToggleRequested += _onProfileDictationToggleRequested;
            backend.ProfileDictationStartRequested += _onProfileDictationStartRequested;
            backend.ProfileDictationStopRequested += _onProfileDictationStopRequested;
            backend.ProfileTextProcessingRequested += _onProfileTextProcessingRequested;
            backend.Failed += _onBackendFailed;
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
            Trace.WriteLine(
                "[HotkeyService] Refusing dictation hotkey that collides with another shortcut."
            );
            return;
        }

        _key = key;
        _modifiers = modifiers;
        PushShortcutsIfRunning();
    }

    public void SetPromptPaletteHotkey(KeyCode? key, ModifierMask modifiers)
    {
        if (
            key is not null
            && HotkeyMatchesAny(key.Value, modifiers, GetBoundHotkeys(HotkeyBinding.PromptPalette))
        )
        {
            Trace.WriteLine(
                "[HotkeyService] Refusing prompt palette hotkey that collides with another shortcut."
            );
            return;
        }

        _promptPaletteKey = key;
        _promptPaletteModifiers = key is null ? ModifierMask.None : modifiers;
        PushShortcutsIfRunning();
    }

    /// <summary>
    ///     Parses strings like "Ctrl+Shift+Space", "Alt+F9", "Ctrl+K" and binds
    ///     them. Returns true on success. Accepts modifier tokens (Ctrl, Shift,
    ///     Alt, Meta/Win/Super) and either a single letter, a digit, a function
    ///     key (F1-F24), or a named key (Space, Enter, Tab, Escape, arrows, etc.).
    ///     Invalid input leaves the current binding unchanged.
    /// </summary>
    public bool TrySetHotkeyFromString(string text)
    {
        if (!TryParseHotkey(text, out var key, out var modifiers))
        {
            return false;
        }

        // Don't let the dictation hotkey collide with another configured
        // binding — the matcher orders cancel/palette/etc. ahead of dictation
        // so a collision would shadow this key.
        if (HotkeyMatchesAny(key!.Value, modifiers, GetBoundHotkeys(HotkeyBinding.Dictation)))
        {
            return false;
        }

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
        {
            return false;
        }

        if (HotkeyMatchesAny(key!.Value, modifiers, GetBoundHotkeys(HotkeyBinding.PromptPalette)))
        {
            return false;
        }

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
        {
            return false;
        }

        if (
            HotkeyMatchesAny(
                key!.Value,
                modifiers,
                GetBoundHotkeys(HotkeyBinding.RecentTranscriptions)
            )
        )
        {
            return false;
        }

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
        {
            return false;
        }

        if (
            HotkeyMatchesAny(
                key!.Value,
                modifiers,
                GetBoundHotkeys(HotkeyBinding.CopyLastTranscription)
            )
        )
        {
            return false;
        }

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
        {
            return false;
        }

        if (
            HotkeyMatchesAny(
                key!.Value,
                modifiers,
                GetBoundHotkeys(HotkeyBinding.TransformSelection)
            )
        )
        {
            return false;
        }

        _transformSelectionKey = key;
        _transformSelectionModifiers = modifiers;
        PushShortcutsIfRunning();
        return true;
    }

    /// <summary>
    ///     Compatibility shim for callers (notably tests) — forwards to
    ///     <see cref="ShortcutMatcher.ModifiersMatch" />.
    /// </summary>
    internal static bool ModifiersMatch(ModifierMask pressed, ModifierMask required)
    {
        return ShortcutMatcher.ModifiersMatch(pressed, required);
    }

    public event EventHandler? DictationToggleRequested;
    public event EventHandler? DictationStartRequested;
    public event EventHandler? DictationStopRequested;
    public event EventHandler? PromptPaletteRequested;
    public event EventHandler? RecentTranscriptionsRequested;
    public event EventHandler? CopyLastTranscriptionRequested;
    public event EventHandler? TransformSelectionRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler<string>? PromptActionHotkeyTriggered;
    public event EventHandler<string>? ProfileDictationToggleRequested;
    public event EventHandler<string>? ProfileDictationStartRequested;
    public event EventHandler? ProfileDictationStopRequested;
    public event EventHandler<string>? ProfileTextProcessingRequested;
    public event EventHandler<string>? HookFailed;

    // Placeholder for instrumentation on backend switch; left as a named
    // call site so future hooks (telemetry, tests) have an obvious anchor.
    private static void OnPropertyChangedHook() { }

    private void UnsubscribeBackendHandlers(IGlobalShortcutBackend? backend)
    {
        if (backend is null)
        {
            return;
        }

        if (_onDictationToggleRequested is not null)
        {
            backend.DictationToggleRequested -= _onDictationToggleRequested;
        }

        if (_onDictationStartRequested is not null)
        {
            backend.DictationStartRequested -= _onDictationStartRequested;
        }

        if (_onDictationStopRequested is not null)
        {
            backend.DictationStopRequested -= _onDictationStopRequested;
        }

        if (_onPromptPaletteRequested is not null)
        {
            backend.PromptPaletteRequested -= _onPromptPaletteRequested;
        }

        if (_onRecentTranscriptionsRequested is not null)
        {
            backend.RecentTranscriptionsRequested -= _onRecentTranscriptionsRequested;
        }

        if (_onCopyLastTranscriptionRequested is not null)
        {
            backend.CopyLastTranscriptionRequested -= _onCopyLastTranscriptionRequested;
        }

        if (_onTransformSelectionRequested is not null)
        {
            backend.TransformSelectionRequested -= _onTransformSelectionRequested;
        }

        if (_onCancelRequested is not null)
        {
            backend.CancelRequested -= _onCancelRequested;
        }

        if (_onPromptActionRequested is not null)
        {
            backend.PromptActionRequested -= _onPromptActionRequested;
        }

        if (_onProfileDictationToggleRequested is not null)
        {
            backend.ProfileDictationToggleRequested -= _onProfileDictationToggleRequested;
        }

        if (_onProfileDictationStartRequested is not null)
        {
            backend.ProfileDictationStartRequested -= _onProfileDictationStartRequested;
        }

        if (_onProfileDictationStopRequested is not null)
        {
            backend.ProfileDictationStopRequested -= _onProfileDictationStopRequested;
        }

        if (_onProfileTextProcessingRequested is not null)
        {
            backend.ProfileTextProcessingRequested -= _onProfileTextProcessingRequested;
        }

        if (_onBackendFailed is not null)
        {
            backend.Failed -= _onBackendFailed;
        }

        _onDictationToggleRequested = null;
        _onDictationStartRequested = null;
        _onDictationStopRequested = null;
        _onPromptPaletteRequested = null;
        _onRecentTranscriptionsRequested = null;
        _onCopyLastTranscriptionRequested = null;
        _onTransformSelectionRequested = null;
        _onCancelRequested = null;
        _onPromptActionRequested = null;
        _onProfileDictationToggleRequested = null;
        _onProfileDictationStartRequested = null;
        _onProfileDictationStopRequested = null;
        _onProfileTextProcessingRequested = null;
        _onBackendFailed = null;
    }

    private GlobalShortcutSet BuildShortcutSet()
    {
        return new GlobalShortcutSet(
            _key,
            _modifiers,
            _promptPaletteKey,
            _promptPaletteModifiers,
            _recentTranscriptionsKey,
            _recentTranscriptionsModifiers,
            _copyLastTranscriptionKey,
            _copyLastTranscriptionModifiers,
            _transformSelectionKey,
            _transformSelectionModifiers,
            _cancelKey,
            _cancelModifiers,
            _mode,
            _cancelShortcutEnabled,
            _promptActionHotkeys,
            _profileHotkeys
        );
    }

    /// <summary>
    ///     Replaces the dynamic per-action hotkey list atomically. Entries that
    ///     collide with an existing fixed binding (Dictation, PromptPalette,
    ///     RecentTranscriptions, CopyLastTranscription, TransformSelection) or
    ///     with an earlier accepted prompt-action entry are dropped with a
    ///     <see cref="Trace.WriteLine" />, matching the silent-rejection style
    ///     of <c>TrySet*HotkeyFromString</c>. Pushes a fresh snapshot to the
    ///     backend so the matcher sees the new list immediately.
    /// </summary>
    public void SetPromptActionHotkeys(IReadOnlyList<PromptActionHotkey> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        // Clear the previous list before reconciling so GetBoundHotkeys()
        // doesn't report the same chord as already-bound when an unchanged
        // entry is re-submitted (the common case — ActionsChanged fires on
        // every add/update/delete and reuses most existing entries). Intra-
        // batch deduplication is handled by the `accepted.Any(...)` check
        // below.
        _promptActionHotkeys = Array.Empty<PromptActionHotkey>();

        var accepted = new List<PromptActionHotkey>(entries.Count);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.ActionId))
            {
                Trace.WriteLine(
                    "[HotkeyService] Refusing prompt-action hotkey with empty action id."
                );
                continue;
            }

            if (HotkeyMatchesAny(entry.Key, entry.Modifiers, GetBoundHotkeys()))
            {
                Trace.WriteLine(
                    $"[HotkeyService] Refusing prompt-action hotkey for '{entry.ActionId}' that collides with another shortcut."
                );
                continue;
            }

            // Intra-batch collision: reuse the full HotkeyMatches so the
            // prefix-collision rule applies between prompt-action entries
            // too (e.g. a batch containing both `Left Ctrl` and `Ctrl+F9`
            // would otherwise accept both and shadow the chord at runtime).
            if (
                accepted.Any(prior =>
                    HotkeyMatches(entry.Key, entry.Modifiers, prior.Key, prior.Modifiers)
                )
            )
            {
                Trace.WriteLine(
                    $"[HotkeyService] Refusing prompt-action hotkey for '{entry.ActionId}' that collides with an earlier entry."
                );
                continue;
            }

            accepted.Add(entry);
        }

        _promptActionHotkeys = accepted;
        PushShortcutsIfRunning();
    }

    /// <summary>
    ///     Translates the JSON-stored <see cref="PromptAction.HotkeyKey" />
    ///     strings into parsed <see cref="PromptActionHotkey" /> entries.
    ///     Actions with a missing or unparseable hotkey are silently skipped
    ///     (matches the <c>TrySet*HotkeyFromString</c> rejection pattern); the
    ///     caller decides what to do with the resulting list (typically pass
    ///     it to <see cref="SetPromptActionHotkeys" />).
    /// </summary>
    public static IReadOnlyList<PromptActionHotkey> ParsePromptActionHotkeys(
        IEnumerable<PromptAction> actions
    )
    {
        ArgumentNullException.ThrowIfNull(actions);

        var result = new List<PromptActionHotkey>();
        foreach (var action in actions)
        {
            if (!action.IsEnabled || string.IsNullOrWhiteSpace(action.HotkeyKey))
            {
                continue;
            }

            if (!TryParseHotkey(action.HotkeyKey, out var key, out var modifiers) || key is null)
            {
                Trace.WriteLine(
                    $"[HotkeyService] Unparseable prompt-action hotkey for '{action.Id}': '{action.HotkeyKey}'."
                );
                continue;
            }

            result.Add(new PromptActionHotkey(action.Id, key.Value, modifiers));
        }

        return result;
    }

    /// <summary>
    ///     Replaces the per-profile hotkey list atomically. Clone of
    ///     <see cref="SetPromptActionHotkeys" />: entries with an empty profile
    ///     id, or that collide with a fixed binding (<see cref="GetBoundHotkeys" />
    ///     — which now also covers prompt-action and other profile chords), or
    ///     with an earlier accepted entry in this batch, are dropped with a
    ///     <see cref="Trace.WriteLine" />. Pushes a fresh snapshot so the matcher
    ///     sees the new list immediately.
    /// </summary>
    public void SetProfileHotkeys(IReadOnlyList<ProfileHotkey> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        // Clear first so GetBoundHotkeys() doesn't report a re-submitted
        // unchanged entry as already-bound (ProfilesChanged fires on every
        // add/update/delete and reuses most existing entries). Intra-batch
        // dedup is handled by the accepted.Any(...) check below.
        _profileHotkeys = Array.Empty<ProfileHotkey>();

        var accepted = new List<ProfileHotkey>(entries.Count);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.ProfileId))
            {
                Trace.WriteLine(
                    "[HotkeyService] Refusing profile hotkey with empty profile id."
                );
                continue;
            }

            if (HotkeyMatchesAny(entry.Key, entry.Modifiers, GetBoundHotkeys()))
            {
                Trace.WriteLine(
                    $"[HotkeyService] Refusing profile hotkey for '{entry.ProfileId}' that collides with another shortcut."
                );
                continue;
            }

            if (
                accepted.Any(prior =>
                    HotkeyMatches(entry.Key, entry.Modifiers, prior.Key, prior.Modifiers)
                )
            )
            {
                Trace.WriteLine(
                    $"[HotkeyService] Refusing profile hotkey for '{entry.ProfileId}' that collides with an earlier entry."
                );
                continue;
            }

            accepted.Add(entry);
        }

        _profileHotkeys = accepted;
        PushShortcutsIfRunning();
    }

    /// <summary>
    ///     Translates the JSON-stored <see cref="Profile.HotkeyData" /> chords
    ///     into parsed <see cref="ProfileHotkey" /> entries, carrying each
    ///     profile's <see cref="Profile.HotkeyBehavior" />. Disabled profiles,
    ///     blank chords, and unparseable chords are skipped (matching
    ///     <see cref="ParsePromptActionHotkeys" />). The caller typically passes
    ///     the result to <see cref="SetProfileHotkeys" />.
    /// </summary>
    public static IReadOnlyList<ProfileHotkey> ParseProfileHotkeys(
        IEnumerable<Profile> profiles
    )
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var result = new List<ProfileHotkey>();
        foreach (var profile in profiles)
        {
            if (!profile.IsEnabled || string.IsNullOrWhiteSpace(profile.HotkeyData))
            {
                continue;
            }

            if (!TryParseHotkey(profile.HotkeyData, out var key, out var modifiers) || key is null)
            {
                Trace.WriteLine(
                    $"[HotkeyService] Unparseable profile hotkey for '{profile.Id}': '{profile.HotkeyData}'."
                );
                continue;
            }

            result.Add(new ProfileHotkey(profile.Id, key.Value, modifiers, profile.HotkeyBehavior));
        }

        return result;
    }

    private void PushShortcutsIfRunning()
    {
        IGlobalShortcutBackend? backend;
        GlobalShortcutSet snapshot;
        lock (_lock)
        {
            backend = _backend;
            if (backend is null)
            {
                return;
            }

            snapshot = BuildShortcutSet();
            // Chain on the previous registration so a burst of changes applies
            // in order. Each link observes the result and surfaces failures
            // through HookFailed; the chain itself never throws because every
            // exception is caught inside the continuation.
            _pendingBackendUpdate = _pendingBackendUpdate
                .ContinueWith(
                    async _ =>
                    {
                        try
                        {
                            var result = await backend
                                .RegisterAsync(snapshot, CancellationToken.None)
                                .ConfigureAwait(false);
                            _backendRequiresToggleMode = result.RequiresToggleMode;
                            if (!result.Success)
                            {
                                var message =
                                    result.UserMessage
                                    ?? $"Backend '{result.BackendId}' rejected the shortcut registration.";
                                HookFailed?.Invoke(this, message);
                            }
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine(
                                $"[HotkeyService] Backend registration threw: {ex.Message}"
                            );
                            HookFailed?.Invoke(this, ex.Message);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                )
                .Unwrap();
        }
    }

    private static bool HotkeyMatches(
        KeyCode key,
        ModifierMask modifiers,
        KeyCode? otherKey,
        ModifierMask otherModifiers
    )
    {
        if (otherKey is null)
        {
            return false;
        }

        // Use ShortcutMatcher.ModifiersMatch so collision detection treats
        // LeftCtrl/RightCtrl (and Shift/Alt/Meta variants) as equivalent —
        // otherwise a chord like RightCtrl+Space could slip past the check
        // and collide with a LeftCtrl+Space binding at runtime, since the
        // dispatcher uses ModifiersMatch when resolving presses.
        if (key == otherKey.Value
            && ShortcutMatcher.ModifiersMatch(modifiers, otherModifiers))
        {
            return true;
        }

        // B8 prefix collision: a side-specific single-modifier binding (e.g.
        // `Left Ctrl` → `(VcLeftControl, None)`) fires on the bare modifier
        // press, which is also the first keystroke of every chord that uses
        // the same physical modifier. Reject either direction at config
        // time so users can't shadow their own chord bindings. The chord
        // side is checked against BOTH left and right flags because the
        // matcher collapses them — pressing the modifier-only's bound side
        // also satisfies any chord storing the opposite-side flag.
        if (CollidesAsModifierPrefix(key, modifiers, otherKey.Value, otherModifiers)
            || CollidesAsModifierPrefix(otherKey.Value, otherModifiers, key, modifiers))
        {
            return true;
        }

        return false;
    }

    private static bool CollidesAsModifierPrefix(
        KeyCode modifierOnlyKey,
        ModifierMask modifierOnlyMods,
        KeyCode chordKey,
        ModifierMask chordMods
    )
    {
        if (modifierOnlyMods != ModifierMask.None)
        {
            return false;
        }

        var physicalGroup = PhysicalModifierGroup(modifierOnlyKey);
        if (physicalGroup == ModifierMask.None)
        {
            return false;
        }

        // The chord must use the same physical modifier (in either side
        // flag — the matcher collapses them) AND must have a different
        // terminal key, otherwise the existing exact-match branch already
        // caught the collision and we'd double-count.
        if (chordKey == modifierOnlyKey && chordMods == modifierOnlyMods)
        {
            return false;
        }

        return (chordMods & physicalGroup) != ModifierMask.None;
    }

    private static ModifierMask PhysicalModifierGroup(KeyCode key)
    {
        return key switch
        {
            KeyCode.VcLeftControl or KeyCode.VcRightControl
                => ModifierMask.LeftCtrl | ModifierMask.RightCtrl,
            KeyCode.VcLeftShift or KeyCode.VcRightShift
                => ModifierMask.LeftShift | ModifierMask.RightShift,
            KeyCode.VcLeftAlt or KeyCode.VcRightAlt
                => ModifierMask.LeftAlt | ModifierMask.RightAlt,
            KeyCode.VcLeftMeta or KeyCode.VcRightMeta
                => ModifierMask.LeftMeta | ModifierMask.RightMeta,
            _ => ModifierMask.None
        };
    }

    private IEnumerable<(KeyCode? Key, ModifierMask Modifiers)> GetBoundHotkeys(
        HotkeyBinding? exclude = null
    )
    {
        if (exclude != HotkeyBinding.Dictation)
        {
            yield return (_key, _modifiers);
        }

        if (exclude != HotkeyBinding.PromptPalette)
        {
            yield return (_promptPaletteKey, _promptPaletteModifiers);
        }

        if (exclude != HotkeyBinding.RecentTranscriptions)
        {
            yield return (_recentTranscriptionsKey, _recentTranscriptionsModifiers);
        }

        if (exclude != HotkeyBinding.CopyLastTranscription)
        {
            yield return (_copyLastTranscriptionKey, _copyLastTranscriptionModifiers);
        }

        if (exclude != HotkeyBinding.TransformSelection)
        {
            yield return (_transformSelectionKey, _transformSelectionModifiers);
        }

        // Dynamic per-action prompt-action bindings (B12). Including them
        // here makes the collision check symmetric: TrySet*HotkeyFromString
        // rejects a fixed-binding change that would shadow an existing
        // prompt-action chord, mirroring SetPromptActionHotkeys' rejection
        // of a new entry that collides with a fixed binding. The
        // PromptAction enum value is intentionally absent — SetPromptActionHotkeys
        // clears _promptActionHotkeys before its reconcile loop, so this
        // method never has to exclude a "current prompt action" entry.
        foreach (var entry in _promptActionHotkeys)
        {
            yield return (entry.Key, entry.Modifiers);
        }

        // Per-profile bindings, for the same symmetry reason as the
        // prompt-action loop above. SetProfileHotkeys clears _profileHotkeys
        // before its reconcile loop, so this never reports a "current profile"
        // entry against itself.
        foreach (var entry in _profileHotkeys)
        {
            yield return (entry.Key, entry.Modifiers);
        }
    }

    private static bool HotkeyMatchesAny(
        KeyCode key,
        ModifierMask modifiers,
        IEnumerable<(KeyCode? Key, ModifierMask Modifiers)> others
    )
    {
        return others.Any(other => HotkeyMatches(key, modifiers, other.Key, other.Modifiers));
    }

    private static string FormatHotkey(KeyCode key, ModifierMask mods)
    {
        // Tier-A side-specific single modifier round-trip: the binding's "key"
        // is itself a side-specific modifier with no extra mods. Emit the
        // parser-symmetric spelling so format → parse → format is stable.
        if (mods == ModifierMask.None)
        {
            var sideSpecific = key switch
            {
                KeyCode.VcLeftControl => "Left Ctrl",
                KeyCode.VcRightControl => "Right Ctrl",
                KeyCode.VcLeftShift => "Left Shift",
                KeyCode.VcRightShift => "Right Shift",
                KeyCode.VcLeftAlt => "Left Alt",
                KeyCode.VcRightAlt => "Right Alt",
                KeyCode.VcLeftMeta => "Left Meta",
                KeyCode.VcRightMeta => "Right Meta",
                _ => null
            };
            if (sideSpecific is not null)
            {
                return sideSpecific;
            }
        }

        var parts = new List<string>();
        if (mods.HasFlag(ModifierMask.LeftCtrl) || mods.HasFlag(ModifierMask.RightCtrl))
        {
            parts.Add("Ctrl");
        }

        if (mods.HasFlag(ModifierMask.LeftShift) || mods.HasFlag(ModifierMask.RightShift))
        {
            parts.Add("Shift");
        }

        if (mods.HasFlag(ModifierMask.LeftAlt) || mods.HasFlag(ModifierMask.RightAlt))
        {
            parts.Add("Alt");
        }

        if (mods.HasFlag(ModifierMask.LeftMeta) || mods.HasFlag(ModifierMask.RightMeta))
        {
            parts.Add("Meta");
        }

        var keyName = key.ToString();
        if (keyName.StartsWith("Vc", StringComparison.Ordinal))
        {
            keyName = keyName[2..];
        }

        parts.Add(keyName);
        return string.Join('+', parts);
    }

    private static bool TryParseHotkey(string text, out KeyCode? key, out ModifierMask modifiers)
    {
        key = null;
        modifiers = ModifierMask.None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Tier-A side-specific single modifier: "Right Alt", "Left Ctrl", etc.
        // Checked before the '+' split so a stray chord like "Right Alt+R"
        // falls through to the normal loop instead of silently absorbing the
        // side prefix here.
        var trimmed = text.Trim();
        if (!trimmed.Contains('+')
            && TryParseSideSpecificSingleModifier(trimmed, out var sideModifierKey))
        {
            key = sideModifierKey;
            modifiers = ModifierMask.None;
            return true;
        }

        var parts = text.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        if (parts.Length == 0)
        {
            return false;
        }

        foreach (var raw in parts)
        {
            var part = raw.ToLowerInvariant();
            switch (part)
            {
                case "ctrl" or "control":
                    modifiers |= ModifierMask.LeftCtrl;
                    continue;
                case "shift":
                    modifiers |= ModifierMask.LeftShift;
                    continue;
                case "alt":
                    modifiers |= ModifierMask.LeftAlt;
                    continue;
                case "meta" or "super" or "win":
                    modifiers |= ModifierMask.LeftMeta;
                    continue;
            }

            if (part.Length == 1 && part[0] is >= 'a' and <= 'z')
            {
                if (key is not null)
                {
                    return false;
                }

                key = (KeyCode)Enum.Parse(typeof(KeyCode), $"Vc{char.ToUpperInvariant(part[0])}");
                continue;
            }

            if (part.Length == 1 && part[0] is >= '0' and <= '9')
            {
                if (key is not null)
                {
                    return false;
                }

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
                _ => (KeyCode?)null
            };
            if (named is not null)
            {
                if (key is not null)
                {
                    return false;
                }

                key = named.Value;
                continue;
            }

            if (
                part.Length is >= 2 and <= 3
                && part[0] == 'f'
                && int.TryParse(part[1..], out var fNum)
                && fNum is >= 1 and <= 24
            )
            {
                if (key is not null)
                {
                    return false;
                }

                key = (KeyCode)Enum.Parse(typeof(KeyCode), $"VcF{fNum}");
                continue;
            }

            key = null;
            return false;
        }

        return key is not null;
    }

    private static bool TryParseSideSpecificSingleModifier(string token, out KeyCode key)
    {
        key = token.ToLowerInvariant() switch
        {
            "left ctrl" or "left control" => KeyCode.VcLeftControl,
            "right ctrl" or "right control" => KeyCode.VcRightControl,
            "left shift" => KeyCode.VcLeftShift,
            "right shift" => KeyCode.VcRightShift,
            "left alt" => KeyCode.VcLeftAlt,
            "right alt" => KeyCode.VcRightAlt,
            "left meta" or "left super" or "left win" => KeyCode.VcLeftMeta,
            "right meta" or "right super" or "right win" => KeyCode.VcRightMeta,
            _ => KeyCode.VcUndefined
        };
        return key != KeyCode.VcUndefined;
    }

    private enum HotkeyBinding
    {
        Dictation,
        PromptPalette,
        RecentTranscriptions,
        CopyLastTranscription,
        TransformSelection
    }
}