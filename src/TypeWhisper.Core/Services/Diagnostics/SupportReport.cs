// The section records are the report's JSON schema: System.Text.Json reads every positional property,
// and the person inspecting the exported file is the consumer, so nothing in-tree needs to read them.
// ReSharper disable NotAccessedPositionalProperty.Global
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypeWhisper.Core.Services.Diagnostics;

/// <summary>
///     A support snapshot of the app's current state. Every section is an explicit projection of technical
///     metadata, never raw settings, user content or exception messages. A section is null when it could not be
///     collected; <see cref="CollectionErrors" /> then names it with only the exception type and HResult.
/// </summary>
public sealed record SupportReport(
    DateTimeOffset ExportedAt,
    SupportReport.AppInfo? App,
    SupportReport.SystemInfo? System,
    SupportReport.SessionInfo? Session,
    SupportReport.ModelInfo? Model,
    SupportReport.ApiInfo? Api,
    SupportReport.AudioInfo? Audio,
    SupportReport.SettingsInfo? Settings,
    SupportReport.PluginsInfo? Plugins,
    SupportReport.ProfilesInfo? Profiles,
    SupportReport.PromptsInfo? Prompts,
    SupportReport.CountsInfo? Counts,
    SupportReport.LogInfo? Log,
    IReadOnlyList<SupportReport.CollectionError> CollectionErrors
)
{
    /// <summary>Placeholder for an identifier that could carry a path, URL or free text.</summary>
    public const string Omitted = "[omitted]";

    private const int MaximumIdentifierLength = 160;

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The Linux support-report schema; bump it when a field changes meaning or is removed.</summary>
    // Instance members, because System.Text.Json does not serialize static ones.
    [JsonPropertyOrder(-2)]
#pragma warning disable CA1822
    // ReSharper disable once UnusedMember.Global
    public int SchemaVersion => 1;

    [JsonPropertyOrder(-1)]
    // ReSharper disable once UnusedMember.Global
    public string Platform => "linux";
#pragma warning restore CA1822

    /// <summary>App identity and process lifetime, without installation paths.</summary>
    public sealed record AppInfo(string Version, string Install, bool DebugBuild, double UptimeSeconds);

    /// <summary>Kernel, distribution, runtime and process memory, without user or machine names.</summary>
    public sealed record SystemInfo(
        string KernelVersion,
        string? DistributionId,
        string? DistributionVersion,
        string OsArchitecture,
        string ProcessArchitecture,
        string RuntimeVersion,
        int ProcessorCount,
        string Locale,
        string? InterfaceLanguage,
        int UtcOffsetMinutes,
        long WorkingSetBytes,
        long ManagedHeapBytes
    );

    /// <summary>The desktop session the app runs in, which decides the hotkey and insertion backends.</summary>
    public sealed record SessionInfo(
        string Type,
        IReadOnlyList<string> Desktops,
        bool HasWaylandDisplay,
        bool HasX11Display,
        bool EvdevHotkeysEnabled
    );

    /// <summary>The selected transcription engine and its published readiness; reading it never loads a model.</summary>
    public sealed record ModelInfo(
        string? EngineId,
        string? ModelId,
        bool EngineAvailable,
        string Status,
        bool SupportsStreaming,
        bool SupportsTranslation,
        string AccelerationPreference,
        string? ActiveAccelerationBackend
    );

    /// <summary>Local API state; the token and discovery file are never read.</summary>
    public sealed record ApiInfo(bool Enabled, bool Running, int Port, bool HasStoredToken);

    /// <summary>Capture state and devices, without device names or persistent hardware identifiers.</summary>
    public sealed record AudioInfo(
        bool NativeAudioAvailable,
        bool Recording,
        bool FollowsSystemDefault,
        bool HasConfiguredDevice,
        bool ConfiguredDeviceAvailable,
        IReadOnlyList<InputDeviceInfo> InputDevices
    );

    /// <summary>One capture device, numbered only within this report.</summary>
    public sealed record InputDeviceInfo(int Ordinal, bool IsDefault, bool IsSelected, int Channels);

    /// <summary>Known behavior choices; this is not a settings backup.</summary>
    public sealed record SettingsInfo(
        string RecordingMode,
        string Language,
        int LanguageHintCount,
        string Task,
        bool LivePreviewEnabled,
        bool LiveStreamingEnabled,
        bool OnlineBatchLivePreviewEnabled,
        bool AutoPaste,
        bool LockPasteToFocusedField,
        string CleanupLevel,
        string SpokenFormattingStrategy,
        bool NumberNormalizationEnabled,
        bool ShortUtterancePunctuationEnabled,
        bool TranscribeShortQuietClipsAggressively,
        bool WhisperModeEnabled,
        bool AudioDuckingEnabled,
        float AudioDuckingLevel,
        bool PauseMediaDuringRecording,
        bool SoundFeedbackEnabled,
        bool SpokenFeedbackEnabled,
        bool SilenceAutoStopEnabled,
        int SilenceAutoStopSeconds,
        bool SaveToHistory,
        string HistoryRetentionMode,
        int HistoryRetentionMinutes,
        int RecoveryRetentionDays,
        int ModelAutoUnloadSeconds,
        bool CustomModelStorage,
        bool VocabularyBoostingEnabled,
        bool AcousticVocabularyBoostingEnabled,
        bool AutoAddDictionaryCorrections,
        bool TargetAppCorrectionLearningEnabled,
        bool CommandModeEnabled,
        bool MemoryEnabled,
        bool CaptureLlmProvenance,
        bool CrashReportingEnabled,
        int EnabledDictionaryPackCount
    );

    /// <summary>Installed plugins and the number of plugin folders that failed to load.</summary>
    public sealed record PluginsInfo(int LoadFailures, IReadOnlyList<PluginInfo> Installed);

    /// <summary>
    ///     A plugin package. <see cref="Enabled" /> is the saved choice and <see cref="Active" /> whether it is
    ///     running, so an enabled plugin that is not active failed to activate.
    /// </summary>
    public sealed record PluginInfo(
        string? Id,
        string? Version,
        string NetworkAccess,
        bool Enabled,
        bool Active,
        IReadOnlyList<PluginRoleInfo> Roles
    );

    /// <summary>A role's last published readiness, read without activating or invoking the plugin.</summary>
    public sealed record PluginRoleInfo(string Kind, string? Id, bool? Ready, string? SelectedModelId);

    /// <summary>Profile configuration without names, IDs, app or URL bindings, or hotkeys.</summary>
    public sealed record ProfilesInfo(int Total, int Enabled, IReadOnlyList<ProfileMetadata> EnabledProfiles);

    /// <summary>Explicit technical metadata of one enabled profile.</summary>
    public sealed record ProfileMetadata(
        int AppBindingCount,
        int UrlBindingCount,
        string ContextMatchMode,
        bool HasHotkey,
        string HotkeyBehavior,
        string StylePreset,
        string? InputLanguage,
        int LanguageHintCount,
        string? Task,
        string? TranslationTarget,
        bool? WhisperModeOverride,
        string? CleanupLevelOverride,
        bool? DeveloperFormattingOverride,
        string? TranscriptionEngineId,
        string? TranscriptionModelId,
        bool HasPrompt
    );

    /// <summary>Prompt configuration without names, IDs or instructions.</summary>
    public sealed record PromptsInfo(
        int Total,
        int Enabled,
        int Presets,
        string? DefaultLlmProviderId,
        IReadOnlyList<PromptMetadata> EnabledPrompts
    );

    /// <summary>Explicit technical metadata of one enabled prompt.</summary>
    public sealed record PromptMetadata(
        bool IsPreset,
        bool IsManualOnly,
        bool HasHotkey,
        string? ProviderId,
        string? ModelId,
        string? ActionPluginId
    );

    /// <summary>Data quantities without any stored text.</summary>
    public sealed record CountsInfo(
        int? HistoryRecords,
        int? DictionaryTerms,
        int? DictionaryCorrections,
        int? EnabledDictionaryEntries,
        int? Snippets,
        int? EnabledSnippets,
        int? ErrorLogEntries
    );

    /// <summary>Retained, allowlisted diagnostic log entries; a disabled log contributes none.</summary>
    public sealed record LogInfo(bool Enabled, int RetentionDays, IReadOnlyList<DiagnosticLogLine> Entries);

    /// <summary>An unavailable section, identified only by exception type and HResult.</summary>
    public sealed record CollectionError(string Section, string? ErrorType, string? HResult);

    /// <summary>Atomically writes the report as one JSON document.</summary>
    /// <exception cref="IOException">The destination could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The destination could not be written.</exception>
    public void Export(string destination) => AtomicFileWrite.WriteAllText(destination, ToJson());

    /// <summary>Serializes the report, reapplying the diagnostic log's admission rules at the export boundary.</summary>
    public string ToJson() => JsonSerializer.Serialize(
        this with
        {
            Log = Log is null
                ? null
                : Log with { Entries = Log.Enabled ? Log.Entries.Select(DiagnosticLogFile.Admit).ToArray() : [] },
        },
        s_json
    );

    /// <summary>Keeps technical IDs such as plugin, provider and model IDs; anything else becomes <see cref="Omitted" />.</summary>
    /// <remarks>
    ///     Namespaced model IDs ("org/model") cannot be told apart from relative paths without a trusted catalog,
    ///     so every directory separator is omitted, as are Windows drive prefixes, spaces, URLs and anything that
    ///     does not start with a letter or digit.
    /// </remarks>
    public static string? Identifier(string? value) =>
        value is null ? null
        : value.Length is > 0 and <= MaximumIdentifierLength
          && char.IsAsciiLetterOrDigit(value[0])
          && !value.Contains("..", StringComparison.Ordinal)
          && !(char.IsAsciiLetter(value[0]) && value is [_, ':', ..])
          && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+' or ':')
            ? value
            : Omitted;
}

/// <summary>Collects report sections independently, so one failing collector does not prevent the export.</summary>
public sealed class SupportReportCapture
{
    private readonly Lock _lock = new();
    private readonly List<SupportReport.CollectionError> _errors = [];

    /// <summary>The failures collected so far.</summary>
    public IReadOnlyList<SupportReport.CollectionError> Errors
    {
        get
        {
            lock (_lock)
            {
                return [.. _errors];
            }
        }
    }

    /// <summary>Returns the section, or records the failure without its message and returns null.</summary>
    public T? Try<T>(string section, Func<T?> collect)
        where T : class
    {
        try
        {
            return collect();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Record(section, ex);
            return null;
        }
    }

    /// <inheritdoc cref="Try{T}" />
    public async Task<T?> TryAsync<T>(string section, Func<Task<T?>> collect)
        where T : class
    {
        try
        {
            return await collect().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Record(section, ex);
            return null;
        }
    }

    private void Record(string section, Exception ex)
    {
        // The log's admission rules reduce the type name and HResult to their allowed formats.
        var admitted = DiagnosticLogFile.Admit(
            new DiagnosticLogLine(
                DateTimeOffset.UtcNow,
                "diagnostics.collection-failed",
                Error: ex.GetType().FullName,
                HResult: $"0x{ex.HResult:X8}"
            )
        );
        lock (_lock)
        {
            _errors.Add(new SupportReport.CollectionError(section, admitted.Error, admitted.HResult));
        }
    }
}
