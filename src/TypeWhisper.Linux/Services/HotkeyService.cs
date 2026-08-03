using SharpHook.Native;
using System.Diagnostics;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Hotkey;

namespace TypeWhisper.Linux.Services;

public enum HotkeyCandidateValidationStatus
{
    Valid,
    Malformed,
    CollidesWithFixedBinding,
    CollidesWithPromptAction,
    CollidesWithProfile,
    MissingEnabledPromptAction,
}

public sealed record HotkeyCandidateValidationResult(
    HotkeyCandidateValidationStatus Status,
    string? NormalizedHotkey
)
{
    public bool IsValid => Status == HotkeyCandidateValidationStatus.Valid;
}

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
    private const KeyCode CancelKey = KeyCode.VcEscape;
    private const ModifierMask CancelModifiers = ModifierMask.None;
    private readonly Lock _lock = new();

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
    private volatile bool _nativeDictationBindingActive;
    private EventHandler<string>? _onBackendFailed;
    private EventHandler? _onCancelRequested;
    private EventHandler? _onCopyLastTranscriptionRequested;
    private EventHandler? _onDictationStartRequested;
    private EventHandler? _onDictationStopRequested;
    private EventHandler? _onDictationDiscardRequested;
    private EventHandler? _onDictationToggleRequested;
    private EventHandler<string>? _onProfileDictationStartRequested;
    private EventHandler? _onProfileDictationStopRequested;
    private EventHandler<string>? _onProfileDictationToggleRequested;
    private EventHandler<string>? _onProfileTextProcessingRequested;
    private EventHandler<string>? _onPromptActionRequested;
    private EventHandler? _onPromptPaletteRequested;
    private EventHandler? _onRecentTranscriptionsRequested;
    private EventHandler? _onTransformSelectionRequested;

    // Serializes backend updates so a burst of TrySet*/Mode= calls applies in order.
    private Task _pendingBackendUpdate = Task.CompletedTask;

    // Latest requested dynamic hotkeys are retained separately from accepted bindings so a
    // rejected candidate can become active when a higher-priority dynamic binding disappears.
    private ProfileHotkey[] _profileHotkeyCandidates = [];
    private PromptActionHotkey[] _promptActionHotkeyCandidates = [];

    // Accepted per-profile hotkeys. Rebuilt wholesale during dynamic reconciliation;
    // backend snapshots capture the list by reference.
    private IReadOnlyList<ProfileHotkey> _profileHotkeys = [];

    // Accepted direct-execution prompt action hotkeys (B12). Rebuilt wholesale during dynamic
    // reconciliation; backend snapshots capture the list by reference.
    private IReadOnlyList<PromptActionHotkey> _promptActionHotkeys =
        [];

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
    ///     True when the current native desktop dictation binding has been verified or applied
    ///     live, so the app-owned fixed dictation route is omitted from backend snapshots.
    /// </summary>
    public bool NativeDictationBindingActive => _nativeDictationBindingActive;

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
        // ReSharper disable once UnusedMember.Global  paired read accessor for a public property whose setter drives PushShortcutsIfRunning; kept as symmetric API
        get => _cancelShortcutEnabled;
        set
        {
            _cancelShortcutEnabled = value;
            PushShortcutsIfRunning();
        }
    }

    public RecordingMode Mode
    {
        // ReSharper disable once UnusedMember.Global  paired read accessor for a public property whose setter drives PushShortcutsIfRunning; kept as symmetric API
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

    public void SetNativeDictationBindingActive(bool active)
    {
        if (_nativeDictationBindingActive == active)
        {
            return;
        }

        _nativeDictationBindingActive = active;
        PushShortcutsIfRunning();
    }

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
            _onDictationDiscardRequested = (_, _) =>
                DictationDiscardRequested?.Invoke(this, EventArgs.Empty);
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
            backend.DictationDiscardRequested += _onDictationDiscardRequested;
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
    ///     Validates a proposed prompt-action chord against the same parser, formatter, fixed
    ///     bindings, and collision matcher used by dynamic reconciliation. The canonical source
    ///     collections are inspected directly so bindings rejected by the current reconciliation
    ///     remain visible. No coordinator or backend state is changed.
    /// </summary>
    public HotkeyCandidateValidationResult ValidatePromptActionHotkeyCandidate(
        string? hotkey,
        string? editedActionId,
        IEnumerable<PromptAction> promptActions,
        IEnumerable<Profile> profiles
    )
    {
        ArgumentNullException.ThrowIfNull(promptActions);
        ArgumentNullException.ThrowIfNull(profiles);

        var parsed = ParseCandidate(hotkey);
        if (
            !parsed.IsValid
            || parsed.NormalizedHotkey is null
            || !TryParseHotkey(parsed.NormalizedHotkey, out var key, out var modifiers)
            || key is null
        )
        {
            return parsed;
        }

        if (HotkeyMatchesAny(key.Value, modifiers, GetFixedHotkeys()))
        {
            return parsed with
            {
                Status = HotkeyCandidateValidationStatus.CollidesWithFixedBinding,
                NormalizedHotkey = null,
            };
        }

        if (
            promptActions.Any(action =>
                action.IsEnabled
                && !string.Equals(action.Id, editedActionId, StringComparison.Ordinal)
                && HotkeyTextMatches(key.Value, modifiers, action.HotkeyKey)
            )
        )
        {
            return parsed with
            {
                Status = HotkeyCandidateValidationStatus.CollidesWithPromptAction,
                NormalizedHotkey = null,
            };
        }

        if (
            profiles.Any(profile =>
                profile.IsEnabled
                && HotkeyTextMatches(key.Value, modifiers, profile.HotkeyData)
            )
        )
        {
            return parsed with
            {
                Status = HotkeyCandidateValidationStatus.CollidesWithProfile,
                NormalizedHotkey = null,
            };
        }

        return parsed;
    }

    /// <summary>
    ///     Validates a proposed profile chord against the canonical fixed and dynamic sources.
    ///     Selected-text bindings additionally require a linked action present in the enabled
    ///     action collection, matching direct prompt-action execution semantics.
    /// </summary>
    public HotkeyCandidateValidationResult ValidateProfileHotkeyCandidate(
        string? hotkey,
        ProfileHotkeyBehavior behavior,
        string? promptActionId,
        string? editedProfileId,
        IEnumerable<PromptAction> promptActions,
        IEnumerable<Profile> profiles
    )
    {
        ArgumentNullException.ThrowIfNull(promptActions);
        ArgumentNullException.ThrowIfNull(profiles);

        var parsed = ParseCandidate(hotkey);
        if (!parsed.IsValid || parsed.NormalizedHotkey is null)
        {
            return parsed;
        }

        var actionSnapshot = promptActions.ToArray();
        if (
            behavior == ProfileHotkeyBehavior.ProcessSelectedText
            && !actionSnapshot.Any(action =>
                action.IsEnabled
                && string.Equals(action.Id, promptActionId, StringComparison.Ordinal)
            )
        )
        {
            return parsed with
            {
                Status = HotkeyCandidateValidationStatus.MissingEnabledPromptAction,
                NormalizedHotkey = null,
            };
        }

        if (!TryParseHotkey(parsed.NormalizedHotkey, out var key, out var modifiers) || key is null)
        {
            return parsed;
        }

        if (HotkeyMatchesAny(key.Value, modifiers, GetFixedHotkeys()))
        {
            return parsed with
            {
                Status = HotkeyCandidateValidationStatus.CollidesWithFixedBinding,
                NormalizedHotkey = null,
            };
        }

        if (
            actionSnapshot.Any(action =>
                action.IsEnabled
                && HotkeyTextMatches(key.Value, modifiers, action.HotkeyKey)
            )
        )
        {
            return parsed with
            {
                Status = HotkeyCandidateValidationStatus.CollidesWithPromptAction,
                NormalizedHotkey = null,
            };
        }

        if (
            profiles.Any(profile =>
                profile.IsEnabled
                && !string.Equals(profile.Id, editedProfileId, StringComparison.Ordinal)
                && HotkeyTextMatches(key.Value, modifiers, profile.HotkeyData)
            )
        )
        {
            return parsed with
            {
                Status = HotkeyCandidateValidationStatus.CollidesWithProfile,
                NormalizedHotkey = null,
            };
        }

        return parsed;
    }

    /// <summary>
    ///     Replaces the requested prompt-action candidates, then reconciles both dynamic lists.
    ///     Rejected candidates remain retained so a later reconciliation can activate them.
    /// </summary>
    // ReSharper disable once UnusedMethodReturnValue.Global  returns the rejection list symmetric with SetDynamicHotkeys; part of the public API contract, no in-tree caller consumes it yet
    public IReadOnlyList<string> SetPromptActionHotkeys(
        IReadOnlyList<PromptActionHotkey> entries
    )
    {
        ArgumentNullException.ThrowIfNull(entries);

        _promptActionHotkeyCandidates = entries.ToArray();
        return ReconcileDynamicHotkeys();
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
    ///     Replaces the requested profile candidates, then reconciles both dynamic lists.
    ///     Rejected candidates remain retained so a later reconciliation can activate them.
    /// </summary>
    // ReSharper disable once UnusedMethodReturnValue.Global  returns the rejection list symmetric with SetDynamicHotkeys; part of the public API contract, no in-tree caller consumes it yet
    public IReadOnlyList<string> SetProfileHotkeys(IReadOnlyList<ProfileHotkey> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        _profileHotkeyCandidates = entries.ToArray();
        return ReconcileDynamicHotkeys();
    }

    /// <summary>
    ///     Atomically replaces both requested dynamic candidate lists and reconciles them once.
    /// </summary>
    public IReadOnlyList<string> SetDynamicHotkeys(
        IReadOnlyList<PromptActionHotkey> promptActions,
        IReadOnlyList<ProfileHotkey> profiles
    )
    {
        ArgumentNullException.ThrowIfNull(promptActions);
        ArgumentNullException.ThrowIfNull(profiles);

        _promptActionHotkeyCandidates = promptActions.ToArray();
        _profileHotkeyCandidates = profiles.ToArray();
        return ReconcileDynamicHotkeys();
    }

    /// <summary>
    ///     Rebuilds accepted dynamic bindings under one deterministic priority: existing fixed
    ///     bindings first, then prompt actions in source order, then profiles in source order.
    /// </summary>
    private List<string> ReconcileDynamicHotkeys()
    {
        // Exclude both previously accepted dynamic lists before capturing fixed bindings. This
        // prevents unchanged candidates from colliding with themselves during a rebuild.
        _promptActionHotkeys = [];
        _profileHotkeys = [];
        var fixedBindings = GetBoundHotkeys().ToArray();
        var acceptedActions = new List<PromptActionHotkey>(
            _promptActionHotkeyCandidates.Length
        );
        var acceptedProfiles = new List<ProfileHotkey>(_profileHotkeyCandidates.Length);
        var rejections = new List<string>();

        foreach (var entry in _promptActionHotkeyCandidates)
        {
            if (string.IsNullOrWhiteSpace(entry.ActionId))
            {
                Trace.WriteLine(
                    "[HotkeyService] Refusing prompt-action hotkey with empty action id."
                );
                rejections.Add(
                    $"Prompt-action hotkey ({FormatHotkey(entry.Key, entry.Modifiers)}) is inactive because its action ID is blank."
                );
                continue;
            }

            if (HotkeyMatchesAny(entry.Key, entry.Modifiers, fixedBindings))
            {
                Trace.WriteLine(
                    $"[HotkeyService] Refusing prompt-action hotkey for '{entry.ActionId}' that collides with another shortcut."
                );
                rejections.Add(DynamicCollisionMessage("Prompt-action", entry.ActionId, entry.Key, entry.Modifiers));
                continue;
            }

            if (
                acceptedActions.Any(prior =>
                    HotkeyMatches(entry.Key, entry.Modifiers, prior.Key, prior.Modifiers)
                )
            )
            {
                Trace.WriteLine(
                    $"[HotkeyService] Refusing prompt-action hotkey for '{entry.ActionId}' that collides with an earlier entry."
                );
                rejections.Add(DynamicCollisionMessage("Prompt-action", entry.ActionId, entry.Key, entry.Modifiers));
                continue;
            }

            acceptedActions.Add(entry);
        }

        foreach (var entry in _profileHotkeyCandidates)
        {
            if (string.IsNullOrWhiteSpace(entry.ProfileId))
            {
                Trace.WriteLine(
                    "[HotkeyService] Refusing profile hotkey with empty profile id."
                );
                rejections.Add(
                    $"Profile hotkey ({FormatHotkey(entry.Key, entry.Modifiers)}) is inactive because its profile ID is blank."
                );
                continue;
            }

            if (
                HotkeyMatchesAny(entry.Key, entry.Modifiers, fixedBindings)
                || acceptedActions.Any(action =>
                    HotkeyMatches(
                        entry.Key,
                        entry.Modifiers,
                        action.Key,
                        action.Modifiers
                    )
                )
            )
            {
                Trace.WriteLine(
                    $"[HotkeyService] Refusing profile hotkey for '{entry.ProfileId}' that collides with another shortcut."
                );
                rejections.Add(DynamicCollisionMessage("Profile", entry.ProfileId, entry.Key, entry.Modifiers));
                continue;
            }

            if (
                acceptedProfiles.Any(prior =>
                    HotkeyMatches(entry.Key, entry.Modifiers, prior.Key, prior.Modifiers)
                )
            )
            {
                Trace.WriteLine(
                    $"[HotkeyService] Refusing profile hotkey for '{entry.ProfileId}' that collides with an earlier entry."
                );
                rejections.Add(DynamicCollisionMessage("Profile", entry.ProfileId, entry.Key, entry.Modifiers));
                continue;
            }

            acceptedProfiles.Add(entry);
        }

        _promptActionHotkeys = acceptedActions;
        _profileHotkeys = acceptedProfiles;
        PushShortcutsIfRunning();
        return rejections;
    }

    private static string DynamicCollisionMessage(
        string bindingKind,
        string id,
        KeyCode key,
        ModifierMask modifiers
    )
    {
        return $"{bindingKind} hotkey '{id}' ({FormatHotkey(key, modifiers)}) is inactive because it conflicts with a higher-priority shortcut.";
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
    public event EventHandler? DictationDiscardRequested;
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

        if (_onDictationDiscardRequested is not null)
        {
            backend.DictationDiscardRequested -= _onDictationDiscardRequested;
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
        _onDictationDiscardRequested = null;
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
        var nativeDictationBindingActive = _nativeDictationBindingActive;
        var suppressCancel = nativeDictationBindingActive && _mode == RecordingMode.PushToTalk;
        return new GlobalShortcutSet(
            nativeDictationBindingActive ? KeyCode.VcUndefined : _key,
            nativeDictationBindingActive ? ModifierMask.None : _modifiers,
            _promptPaletteKey,
            _promptPaletteModifiers,
            _recentTranscriptionsKey,
            _recentTranscriptionsModifiers,
            _copyLastTranscriptionKey,
            _copyLastTranscriptionModifiers,
            _transformSelectionKey,
            _transformSelectionModifiers,
            suppressCancel ? KeyCode.VcUndefined : CancelKey,
            suppressCancel ? ModifierMask.None : CancelModifiers,
            _mode,
            // ReSharper disable once SimplifyConditionalTernaryExpression -- kept parallel with the suppressCancel ? x : y projection lines above for readability.
            suppressCancel ? false : _cancelShortcutEnabled,
            _promptActionHotkeys,
            _profileHotkeys
        );
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

        // Use ShortcutMatcher.ModifiersMatch so LeftCtrl/RightCtrl (and Shift/Alt/Meta
        // variants) are treated as equivalent — same logic the dispatcher uses at runtime.
        if (key == otherKey.Value
            && ShortcutMatcher.ModifiersMatch(modifiers, otherModifiers))
        {
            return true;
        }

        // Prefix collision: a bare-modifier binding (e.g. `Left Ctrl`) fires on the same
        // keypress that opens any chord using that physical modifier. Reject either direction
        // at config time. Check against both Left/Right flags because the matcher collapses them.
        return CollidesAsModifierPrefix(key, modifiers, otherKey.Value, otherModifiers)
            || CollidesAsModifierPrefix(otherKey.Value, otherModifiers, key, modifiers);
    }

    private static HotkeyCandidateValidationResult ParseCandidate(string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            return new HotkeyCandidateValidationResult(
                HotkeyCandidateValidationStatus.Valid,
                null
            );
        }

        if (!TryParseHotkey(hotkey, out var key, out var modifiers) || key is null)
        {
            return new HotkeyCandidateValidationResult(
                HotkeyCandidateValidationStatus.Malformed,
                null
            );
        }

        return new HotkeyCandidateValidationResult(
            HotkeyCandidateValidationStatus.Valid,
            FormatHotkey(key.Value, modifiers)
        );
    }

    private static bool HotkeyTextMatches(
        KeyCode key,
        ModifierMask modifiers,
        string? otherHotkey
    )
    {
        return !string.IsNullOrWhiteSpace(otherHotkey)
            && TryParseHotkey(otherHotkey, out var otherKey, out var otherModifiers)
            && HotkeyMatches(key, modifiers, otherKey, otherModifiers);
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

        // Different terminal key required; same key+mods is already caught by the exact-match branch.
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
            _ => ModifierMask.None,
        };
    }

    private IEnumerable<(KeyCode? Key, ModifierMask Modifiers)> GetBoundHotkeys(
        HotkeyBinding? exclude = null
    )
    {
        foreach (var binding in GetFixedHotkeys(exclude))
        {
            yield return binding;
        }

        // Dynamic prompt-action bindings make collision detection symmetric: fixed-binding
        // changes that would shadow a prompt-action chord are also rejected. Dynamic
        // reconciliation clears both accepted lists before it captures fixed bindings.
        foreach (var entry in _promptActionHotkeys)
        {
            yield return (entry.Key, entry.Modifiers);
        }

        // Per-profile bindings use the same symmetry rule as prompt actions.
        foreach (var entry in _profileHotkeys)
        {
            yield return (entry.Key, entry.Modifiers);
        }
    }

    private IEnumerable<(KeyCode? Key, ModifierMask Modifiers)> GetFixedHotkeys(
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
        // Side-specific single-modifier round-trip: emit parser-symmetric spelling
        // (e.g. "Left Ctrl") so format → parse → format is stable.
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
                _ => null,
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

        // Check side-specific single-modifier spelling before the '+' split so "Right Alt+R"
        // falls through to the normal loop rather than being absorbed as a side prefix.
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

            switch (part)
            {
                case [>= 'a' and <= 'z']:
                    if (key is not null)
                    {
                        return false;
                    }

                    key = Enum.Parse<KeyCode>($"Vc{char.ToUpperInvariant(part[0])}");
                    continue;
                case [>= '0' and <= '9']:
                    if (key is not null)
                    {
                        return false;
                    }

                    key = Enum.Parse<KeyCode>($"Vc{part[0]}");
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

                key = Enum.Parse<KeyCode>($"VcF{fNum}");
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
            _ => KeyCode.VcUndefined,
        };
        return key != KeyCode.VcUndefined;
    }

    private enum HotkeyBinding
    {
        Dictation,
        PromptPalette,
        RecentTranscriptions,
        CopyLastTranscription,
        TransformSelection,
    }
}
