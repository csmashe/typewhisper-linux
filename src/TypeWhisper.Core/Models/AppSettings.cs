namespace TypeWhisper.Core.Models;

/// <summary>
///     The user's full application configuration, persisted to disk and applied
///     across every feature (hotkeys, model/acceleration, audio, overlay,
///     translation, watch folder, API server, dictionary, plugins, and more).
///     Properties are init-only; <see cref="Default" /> yields the shipping
///     defaults and the static helpers normalize or clamp individual values.
/// </summary>
public record AppSettings
{
    public const string DefaultSpokenFeedbackProviderId = "linux-system";

    public const string LocalModelAccelerationAuto = "auto";
    public const string LocalModelAccelerationCpu = "cpu";
    public const string LocalModelAccelerationNvidiaCuda = "nvidia-cuda";

    public const int MinPreviewBubbleAutoHideMilliseconds = 0;
    public const int DefaultPreviewBubbleAutoHideMilliseconds = 1500;
    public const int MaxPreviewBubbleAutoHideMilliseconds = 5000;

    public string ToggleHotkey { get; init; } = "Ctrl+Shift+F9";
    public string PushToTalkHotkey { get; init; } = "Ctrl+Shift";
    public string ToggleOnlyHotkey { get; init; } = "";
    public string HoldOnlyHotkey { get; init; } = "";
    public string RecentTranscriptionsHotkey { get; init; } = "";
    public string CopyLastTranscriptionHotkey { get; init; } = "";
    public string TransformSelectionHotkey { get; init; } = "";
    public string Language { get; init; } = "auto";
    public bool AutoPaste { get; init; } = true;

    public Dictionary<string, TextInsertionStrategy> AppInsertionStrategies
    {
        get;
        init =>
            // JsonSerializer can pass null for a null JSON value; fall back to an empty map
            // instead of letting the Dictionary copy-constructor throw on a null source.
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            field = value is null
                ? new Dictionary<string, TextInsertionStrategy>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, TextInsertionStrategy>(
                    value,
                    StringComparer.OrdinalIgnoreCase
                );
    } = new(StringComparer.OrdinalIgnoreCase);

    public CleanupLevel CleanupLevel { get; init; } = CleanupLevel.None;

    // Hybrid: quick tap toggles, held key acts as push-to-talk. Most forgiving
    // default — avoids the rapid on/off thrash Toggle produces on a held key.
    public RecordingMode Mode { get; init; } = RecordingMode.Hybrid;
    public HistoryRetentionMode HistoryRetentionMode { get; init; } = HistoryRetentionMode.Duration;
    public int HistoryRetentionMinutes { get; init; } = 90 * 24 * 60;
    public int? SelectedMicrophoneDevice { get; init; }
    public string? SelectedMicrophoneDeviceId { get; init; }

    // Model
    public string? SelectedModelId { get; init; }
    public string LocalModelAcceleration { get; init; } = LocalModelAccelerationAuto;

    // Custom on-disk location for large local model assets. Null = default app-data path.
    public string? LocalModelStoragePath { get; init; }

    // Manual file transcription
    public string? FileTranscriptionEngineOverride { get; init; }
    public string? FileTranscriptionModelOverride { get; init; }

    // Cloud Provider API Keys
    public string? GroqApiKey { get; init; }
    public string? OpenAiApiKey { get; init; }

    // Audio features
    public bool WhisperModeEnabled { get; init; }
    public bool AudioDuckingEnabled { get; init; }
    public float AudioDuckingLevel { get; init; } = 0.4f;
    public bool PauseMediaDuringRecording { get; init; }
    public bool SoundFeedbackEnabled { get; init; } = true;
    public bool TranscribeShortQuietClipsAggressively { get; init; }

    // Live transcription (streaming preview while recording)
    public bool LiveTranscriptionEnabled { get; init; } = true;
    public bool LiveTranscriptionStreamingEnabled { get; init; }
    public bool OnlineAsrBatchLiveTranscriptionEnabled { get; init; }

    // Silence detection
    public bool SilenceAutoStopEnabled { get; init; }
    public int SilenceAutoStopSeconds { get; init; } = 10;

    // Overlay
    public OverlayPosition OverlayPosition { get; init; } = OverlayPosition.Bottom;
    public OverlayWidget OverlayLeftWidget { get; init; } = OverlayWidget.Waveform;
    public OverlayWidget OverlayRightWidget { get; init; } = OverlayWidget.Timer;
    public int PreviewBubbleAutoHideMilliseconds { get; init; } = DefaultPreviewBubbleAutoHideMilliseconds;
    public double? OverlayCustomLeft { get; init; }
    public double? OverlayCustomTop { get; init; }

    // Translation
    public string TranscriptionTask { get; init; } = "transcribe";
    public string? TranslationTargetLanguage { get; init; }

    // Watch folder automation
    public string? WatchFolderPath { get; init; }
    public string? WatchFolderOutputPath { get; init; }
    public string WatchFolderOutputFormat { get; init; } = "md";
    public bool WatchFolderAutoStart { get; init; }
    public bool WatchFolderDeleteSource { get; init; }
    public string WatchFolderLanguage { get; init; } = "auto";
    public string? WatchFolderEngineOverride { get; init; }
    public string? WatchFolderModelOverride { get; init; }

    // API Server
    public bool ApiServerEnabled { get; init; }
    public int ApiServerPort { get; init; } = 9876;
    public string? ApiServerBearerToken { get; init; }

    // Dictionary
    public string[] EnabledPackIds { get; init; } = [];
    public bool VocabularyBoostingEnabled { get; init; }
    public bool AutoAddDictionaryCorrections { get; init; }

    // Onboarding
    public bool HasCompletedOnboarding { get; init; }
    public string SelectedIndustryPresetId { get; init; } = "general";

    // Prompt Palette
    public string PromptPaletteHotkey { get; init; } = "";
    public string? DefaultLlmProvider { get; init; }

    // Plugin state
    public Dictionary<string, bool> PluginEnabledState { get; init; } = new();
    public bool PluginFirstRunCompleted { get; init; }

    // Model auto-unload (0 = disabled)
    public int ModelAutoUnloadSeconds { get; init; }

    // History
    public bool SaveToHistoryEnabled { get; init; } = true;

    // Spoken feedback (TTS readback after transcription)
    public bool SpokenFeedbackEnabled { get; init; }
    public string SpokenFeedbackProviderId { get; init; } = DefaultSpokenFeedbackProviderId;
    public string? SpokenFeedbackVoiceId { get; init; }

    // Memory extraction
    public bool MemoryEnabled { get; init; }

    // Reference context for LLM cleanup (opt-in, local-first, both default off).
    // When enabled, a bounded snippet of the focused element's text / the clipboard
    // is passed to Medium/High cleanup as read-only spelling reference.
    public bool ScreenContextEnabled { get; init; }
    public bool ClipboardContextEnabled { get; init; }

    // UI Language (null = auto-detect from system)
    public string? UiLanguage { get; init; }

    // Dashboard
    public int DashboardSelectedPeriod { get; init; }

    // When true (default), uses the evdev backend for global hotkeys on Wayland
    // (reads /dev/input/event*). Disable to fall back to focused-only SharpHook.
    public bool WaylandEvdevHotkeysEnabled { get; init; } = true;

    public static AppSettings Default => new();

    /// <summary>
    ///     Maps a stored/user acceleration string to a canonical value
    ///     (<see cref="LocalModelAccelerationAuto" />, <c>…Cpu</c>, or
    ///     <c>…NvidiaCuda</c>), accepting aliases like "cuda" and "nvidia cuda" and
    ///     falling back to "auto" for blank or unrecognized input.
    /// </summary>
    public static string NormalizeLocalModelAcceleration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return LocalModelAccelerationAuto;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, LocalModelAccelerationAuto, StringComparison.OrdinalIgnoreCase))
        {
            return LocalModelAccelerationAuto;
        }

        if (string.Equals(trimmed, LocalModelAccelerationCpu, StringComparison.OrdinalIgnoreCase))
        {
            return LocalModelAccelerationCpu;
        }

        if (
            string.Equals(trimmed, LocalModelAccelerationNvidiaCuda, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "cuda", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "nvidia cuda", StringComparison.OrdinalIgnoreCase)
        )
        {
            return LocalModelAccelerationNvidiaCuda;
        }

        return LocalModelAccelerationAuto;
    }

    // Treats blank/whitespace as "use the default storage path" (null); trims otherwise.
    public static string? NormalizeLocalModelStoragePath(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static int NormalizePreviewBubbleAutoHideMilliseconds(int milliseconds)
    {
        return Math.Clamp(
            milliseconds,
            MinPreviewBubbleAutoHideMilliseconds,
            MaxPreviewBubbleAutoHideMilliseconds);
    }

    public static (double Left, double Top) ClampOverlayPositionToWorkArea(
        double left,
        double top,
        double workAreaLeft,
        double workAreaTop,
        double workAreaRight,
        double workAreaBottom,
        double windowWidth,
        double windowHeight)
    {
        var maxLeft = Math.Max(workAreaLeft, workAreaRight - windowWidth);
        var maxTop = Math.Max(workAreaTop, workAreaBottom - windowHeight);
        var clampedLeft = Math.Clamp(left, workAreaLeft, maxLeft);
        var clampedTop = Math.Clamp(top, workAreaTop, maxTop);
        return (clampedLeft, clampedTop);
    }
}