using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.Diagnostics;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Report = TypeWhisper.Core.Services.Diagnostics.SupportReport;

namespace TypeWhisper.Linux.Services.Telemetry;

/// <summary>
///     Builds the support report from state the app has already published. Collecting never loads a model,
///     activates a plugin or makes a network call, and works whether or not the diagnostic log is on.
/// </summary>
public sealed class SupportReportService
{
    private const string FirstPartyPrefix = "com.typewhisper.";
    private const string ModelKey = "engine.model";
    private const string ProviderKey = "engine.provider";
    private const string CustomModel = "custom";

    private readonly Sources _sources;

    public SupportReportService(
        ISettingsService settings,
        ModelManagerService models,
        HttpApiService api,
        AudioRecordingService audio,
        IHistoryService history,
        IDictionaryService dictionary,
        ISnippetService snippets,
        IProfileService profiles,
        IPromptActionService prompts,
        IErrorLogService errorLog,
        LocalDiagnosticLogService diagnosticLog
    )
        : this(new Sources(
            () => settings.Current,
            () => CollectTrustedModelProviders(models.PluginManager),
            trusted => CollectModel(models, settings.Current, trusted),
            () => new Report.ApiInfo(
                settings.Current.ApiServerEnabled,
                api.IsRunning,
                settings.Current.ApiServerPort,
                !string.IsNullOrWhiteSpace(settings.Current.ApiServerBearerToken)
            ),
            () => SummarizeAudio(
                AudioRecordingService.GetInputDevices(),
                AudioRecordingService.NativeAudioUnavailableReason is null,
                audio.IsRecording,
                settings.Current
            ),
            trusted => CollectPlugins(models.PluginManager, settings.Current, trusted),
            () => profiles.TryGetProfiles(out var stored) ? stored : throw new StoreUnreadableException(),
            () => prompts.TryGetActions(out var stored) ? stored : throw new StoreUnreadableException(),
            () => history.TryGetRecords(out var records)
                ? records.Count(record => record.Status == TranscriptionRecordStatus.Succeeded)
                : throw new StoreUnreadableException(),
            () => dictionary.TryGetEntries(out var entries) ? entries : throw new StoreUnreadableException(),
            () => snippets.TryGetSnippets(out var stored) ? stored : throw new StoreUnreadableException(),
            () => errorLog.TryGetEntries(out var errors) ? errors.Count : throw new StoreUnreadableException(),
            async () => await diagnosticLog.CaptureForReportAsync().ConfigureAwait(false)
        ))
    {
    }

    internal SupportReportService(Sources sources)
    {
        _sources = sources;
    }

    /// <summary>
    ///     Captures live state on the calling thread, then reads stored data and the diagnostic log in the
    ///     background. A failing section is left out and listed in <see cref="Report.CollectionErrors" />.
    /// </summary>
    public async Task<Report> CaptureAsync()
    {
        var exportedAt = DateTimeOffset.UtcNow;
        var capture = new SupportReportCapture();
        var settings = capture.Try("settings", _sources.Settings);
        var app = capture.Try("app", CollectApp);
        var system = capture.Try("system", CollectSystem);
        var session = capture.Try("session", () => CollectSession(settings));
        // Without the plugin list every model ID is treated as user-supplied and omitted.
        var trusted = capture.Try("modelProviders", _sources.TrustedModelProviders)
                      ?? new HashSet<string>(StringComparer.Ordinal);
        var model = capture.Try("model", () => _sources.Model(trusted));
        var api = capture.Try("api", _sources.Api);
        var audio = capture.Try("audio", _sources.Audio);
        var plugins = capture.Try("plugins", () => _sources.Plugins(trusted));
        var profiles = capture.Try("profiles", () => SummarizeProfiles(_sources.Profiles(), trusted));
        var prompts = capture.Try("prompts", () => SummarizePrompts(_sources.Prompts(), settings?.DefaultLlmProvider, trusted));
        var settingsInfo = settings is null ? null : capture.Try("settings", () => SummarizeSettings(settings));

        var counts = await Task.Run(() =>
        {
            var history = capture.Try("historyCount", () => new Count(_sources.HistoryCount()));
            // Counted inside the collector, so a malformed entry fails only its own section.
            var dictionary = capture.Try("dictionaryCounts", () =>
            {
                var entries = _sources.Dictionary();
                return new DictionaryCounts(
                    entries.Count(entry => entry.EntryType == DictionaryEntryType.Term),
                    entries.Count(entry => entry.EntryType == DictionaryEntryType.Correction),
                    entries.Count(entry => entry.IsEnabled)
                );
            });
            var snippets = capture.Try("snippetCounts", () =>
            {
                var stored = _sources.Snippets();
                return new SnippetCounts(stored.Count, stored.Count(snippet => snippet.IsEnabled));
            });
            var errors = capture.Try("errorLogCount", () => new Count(_sources.ErrorLogCount()));
            return new Report.CountsInfo(
                history?.Value,
                dictionary?.Terms,
                dictionary?.Corrections,
                dictionary?.Enabled,
                snippets?.Total,
                snippets?.Enabled,
                errors?.Value
            );
        }).ConfigureAwait(false);
        var log = await capture.TryAsync("diagnosticLog", _sources.Log).ConfigureAwait(false);
        log = log is null ? null : RedactLogModels(log, trusted);

        return new Report(
            exportedAt,
            app,
            system,
            session,
            model,
            api,
            audio,
            settingsInfo,
            plugins,
            profiles,
            prompts,
            counts,
            log,
            capture.Errors
        );
    }

    private static Report.AppInfo CollectApp()
    {
        using var process = Process.GetCurrentProcess();
        return new Report.AppInfo(
            AppVersion.Display,
            InstallKindDetector.Detect(),
#if DEBUG
            true,
#else
            false,
#endif
            Math.Max(0, (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds)
        );
    }

    private static Report.SystemInfo CollectSystem()
    {
        using var process = Process.GetCurrentProcess();
        var release = ReadOsRelease("/etc/os-release");
        return new Report.SystemInfo(
            Environment.OSVersion.Version.ToString(),
            Report.Identifier(release.GetValueOrDefault("ID")),
            Report.Identifier(release.GetValueOrDefault("VERSION_ID")),
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.Version.ToString(),
            Environment.ProcessorCount,
            CultureInfo.CurrentCulture.Name,
            Report.Identifier(Loc.Instance.CurrentLanguage),
            (int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow).TotalMinutes,
            process.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false)
        );
    }

    /// <summary>Reads the unquoted key/value pairs of an os-release file; a missing file yields none.</summary>
    internal static IReadOnlyDictionary<string, string> ReadOsRelease(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (var line in File.ReadLines(path))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                values[line[..separator].Trim()] = line[(separator + 1)..].Trim().Trim('"', '\'');
            }
        }

        return values;
    }

    private static Report.SessionInfo CollectSession(AppSettings? settings) =>
        SummarizeSession(
            Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
            Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
            Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"),
            Environment.GetEnvironmentVariable("DISPLAY"),
            settings?.WaylandEvdevHotkeysEnabled ?? true
        );

    internal static Report.SessionInfo SummarizeSession(
        string? waylandDisplay,
        string? sessionType,
        string? currentDesktop,
        string? x11Display,
        bool evdevHotkeysEnabled
    ) =>
        new(
            WaylandSessionDetector.IsWaylandSession(waylandDisplay, sessionType)
                ? "wayland"
                : string.Equals(sessionType?.Trim(), "x11", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(x11Display)
                    ? "x11"
                    : "unknown",
            (currentDesktop ?? "")
                .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(desktop => Report.Identifier(desktop)!)
                .ToArray(),
            WaylandSessionDetector.HasWaylandDisplay(waylandDisplay),
            !string.IsNullOrWhiteSpace(x11Display),
            evdevHotkeysEnabled
        );

    /// <summary>
    ///     Provider and selection IDs whose model IDs come from a curated catalog or a fixed service. A model ID
    ///     of a user-controlled endpoint (an OpenAI-compatible server, a script) or of a third-party plugin is a
    ///     name the user or their server chose, so it is omitted.
    /// </summary>
    private static HashSet<string> CollectTrustedModelProviders(PluginManager plugins)
    {
        var trustedPlugins = plugins.AllPlugins
            // The manifest must declare its access: a legacy manifest without it resolves to Local or Network
            // even for a user-controlled endpoint.
            .Where(plugin => plugin.Manifest.Id.StartsWith(FirstPartyPrefix, StringComparison.Ordinal)
                             && plugin.Manifest.NetworkAccess is { } access
                             && access != PluginNetworkAccess.UserControlled)
            .Select(plugin => plugin.Manifest.Id)
            .ToHashSet(StringComparer.Ordinal);
        var trusted = new HashSet<string>(trustedPlugins, StringComparer.Ordinal);
        var untrustedProviders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in plugins.TranscriptionEngines)
        {
            // Log lines name the engine by its provider ID rather than its selection ID.
            var providerId = Read(() => role.ProviderId);
            if (!trustedPlugins.Contains(role.PluginId))
            {
                if (providerId is not null)
                {
                    untrustedProviders.Add(providerId);
                }

                continue;
            }

            if (Read(role.GetTranscriptionSelectionId) is { } id)
            {
                trusted.Add(id);
            }

            if (providerId is not null)
            {
                trusted.Add(providerId);
            }
        }

        // A provider ID claimed by an untrusted engine as well cannot tell their log lines apart.
        trusted.ExceptWith(untrustedProviders);

        foreach (var role in plugins.LlmProviders.Where(role => trustedPlugins.Contains(role.PluginId)))
        {
            if (Read(role.GetLlmSelectionId) is { } id)
            {
                trusted.Add(id);
            }
        }

        return trusted;
    }

    /// <summary>
    ///     The model ID when its provider is trusted and it is not an account's fine-tuned model (whose ID
    ///     carries the organization and a chosen suffix), otherwise <see cref="Report.Omitted" />.
    /// </summary>
    internal static string? ModelId(string? provider, string? model, IReadOnlySet<string> trusted) =>
        string.IsNullOrWhiteSpace(model) ? null
        : IsPublicModel(provider, model, trusted) ? Report.Identifier(model)
        : Report.Omitted;

    // Some lines carry the whole plugin:<selection>:<model> selection instead of the engine's model ID.
    private static bool IsPublicLogModel(string? provider, string model, IReadOnlySet<string> trusted)
    {
        if (!ModelManagerService.IsPluginModel(model))
        {
            return IsPublicModel(provider, model, trusted);
        }

        try
        {
            var (selection, selectedModel) = ModelManagerService.ParsePluginModelId(model);
            return IsPublicModel(selection, selectedModel, trusted);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsPublicModel(string? provider, string model, IReadOnlySet<string> trusted) =>
        provider is not null
        && trusted.Contains(provider)
        && !model.StartsWith("ft:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Applies the model policy to retained log entries: the log keeps every engine's model ID, so one from an
    ///     untrusted provider, or without its provider on the same line, becomes "custom" in the report. The
    ///     trusted set must include the provider IDs of trusted engines, which is what log lines name.
    /// </summary>
    internal static Report.LogInfo RedactLogModels(Report.LogInfo log, IReadOnlySet<string> trusted) =>
        log with
        {
            Entries = log.Entries.Select(entry =>
                entry.Data is { } data
                && data.TryGetValue(ModelKey, out var model)
                && !IsPublicLogModel(data.GetValueOrDefault(ProviderKey), model, trusted)
                    ? entry with
                    {
                        Data = data.ToDictionary(
                            pair => pair.Key,
                            pair => pair.Key == ModelKey ? CustomModel : pair.Value,
                            StringComparer.Ordinal
                        ),
                    }
                    : entry
            ).ToArray(),
        };

    private static Report.ModelInfo CollectModel(ModelManagerService models, AppSettings settings, IReadOnlySet<string> trusted)
    {
        var selected = settings.SelectedModelId;
        var engine = models.GetTranscriptionPlugin(selected);
        var (engineId, modelId) = SplitSelection(selected, trusted);
        return new Report.ModelInfo(
            engineId,
            modelId,
            engine is not null,
            selected is null ? "None" : models.GetStatus(selected).Type.ToString(),
            engine?.SupportsStreaming == true,
            models.SupportsTranslation(selected),
            AppSettings.NormalizeLocalModelAcceleration(settings.LocalModelAcceleration),
            engine?.AccelerationStatus.ActiveBackend.ToString()
        );
    }

    /// <summary>
    ///     Numbers devices within the report and marks the one capture would use, resolved by its stable ID
    ///     exactly as capture resolves it. Names and persistent IDs are never included: both carry the device name.
    /// </summary>
    internal static Report.AudioInfo SummarizeAudio(
        IReadOnlyList<AudioInputDevice> devices,
        bool nativeAudioAvailable,
        bool recording,
        AppSettings settings
    )
    {
        var follows = AudioRecordingService.IsFollowSystemDefault(settings.SelectedMicrophoneDeviceId);
        var configured = !follows
                         && (settings.SelectedMicrophoneDevice.HasValue
                             || !string.IsNullOrWhiteSpace(settings.SelectedMicrophoneDeviceId));
        var selected = AudioRecordingService.ResolveConfiguredDevice(
            devices,
            settings.SelectedMicrophoneDevice,
            settings.SelectedMicrophoneDeviceId
        );
        return new Report.AudioInfo(
            nativeAudioAvailable,
            recording,
            follows,
            configured,
            !configured || selected is not null,
            devices.Select((device, ordinal) => new Report.InputDeviceInfo(
                ordinal + 1,
                device.IsDefault,
                ReferenceEquals(device, selected),
                device.MaxInputChannels
            )).ToArray()
        );
    }

    private static Report.SettingsInfo SummarizeSettings(AppSettings settings) =>
        new(
            settings.Mode.ToString(),
            Report.Identifier(settings.Language) ?? "auto",
            settings.GetLanguageHints().Count,
            Report.Identifier(settings.TranscriptionTask) ?? "transcribe",
            settings.LiveTranscriptionEnabled,
            settings.LiveTranscriptionStreamingEnabled,
            settings.OnlineAsrBatchLiveTranscriptionEnabled,
            settings.AutoPaste,
            settings.LockPasteToFocusedField,
            settings.CleanupLevel.ToString(),
            settings.SpokenFormattingStrategy.ToString(),
            settings.TranscriptionNumberNormalizationEnabled,
            settings.ShortUtterancePunctuationEnabled,
            settings.TranscribeShortQuietClipsAggressively,
            settings.WhisperModeEnabled,
            settings.AudioDuckingEnabled,
            settings.AudioDuckingLevel,
            settings.PauseMediaDuringRecording,
            settings.SoundFeedbackEnabled,
            settings.SpokenFeedbackEnabled,
            settings.SilenceAutoStopEnabled,
            settings.SilenceAutoStopSeconds,
            settings.SaveToHistoryEnabled,
            settings.HistoryRetentionMode.ToString(),
            settings.HistoryRetentionMinutes,
            settings.DictationRecoveryRetentionDays,
            settings.ModelAutoUnloadSeconds,
            !string.IsNullOrWhiteSpace(settings.LocalModelStoragePath),
            settings.VocabularyBoostingEnabled,
            settings.AcousticVocabularyBoostingEnabled,
            settings.AutoAddDictionaryCorrections,
            settings.TargetAppCorrectionLearningEnabled,
            settings.CommandModeEnabled,
            settings.MemoryEnabled,
            settings.CaptureLlmProvenance,
            settings.CrashReportingEnabled,
            settings.EnabledPackIds.Length
        );

    private static Report.PluginsInfo CollectPlugins(PluginManager plugins, AppSettings settings, IReadOnlySet<string> trusted) =>
        SummarizePlugins(
            plugins.AllPlugins.Select(plugin => new PluginState(
                plugin.Manifest,
                plugin.Metadata.NetworkAccess,
                settings.PluginEnabledState.TryGetValue(plugin.Manifest.Id, out var enabled)
                    ? enabled
                    : PluginManager.IsEnabledByDefault(plugin),
                plugins.IsEnabled(plugin.Manifest.Id)
            )).ToArray(),
            new PluginRoles(
                plugins.TranscriptionEngines,
                plugins.LlmProviders,
                plugins.TtsProviders,
                plugins.GetPlugins<IVocabularyRescorerPlugin>(),
                plugins.ActionPlugins
            ),
            plugins.LoadFailures.Count,
            trusted
        );

    internal static Report.PluginsInfo SummarizePlugins(
        IReadOnlyList<PluginState> plugins,
        PluginRoles roles,
        int loadFailures,
        IReadOnlySet<string> trusted
    ) =>
        new(
            loadFailures,
            plugins.Select(plugin =>
            {
                var id = plugin.Manifest.Id;
                var pluginRoles = roles.Transcription
                    .Where(role => role.PluginId == id)
                    .Select(role =>
                    {
                        var selectionId = Read(role.GetTranscriptionSelectionId);
                        return new Report.PluginRoleInfo(
                            "transcription",
                            Report.Identifier(selectionId),
                            Read(() => (bool?)role.IsConfigured),
                            ModelId(selectionId, Read(() => role.SelectedModelId), trusted)
                        );
                    })
                    .Concat(roles.Llm.Where(role => role.PluginId == id).Select(role => new Report.PluginRoleInfo(
                        "llm",
                        Report.Identifier(Read(role.GetLlmSelectionId)),
                        Read(() => (bool?)role.IsAvailable),
                        null
                    )))
                    .Concat(roles.Tts.Where(role => role.PluginId == id).Select(role => new Report.PluginRoleInfo(
                        "tts",
                        Report.Identifier(Read(() => role.ProviderId)),
                        Read(() => (bool?)role.IsConfigured),
                        null
                    )))
                    .Concat(roles.Vocabulary.Where(role => role.PluginId == id).Select(role =>
                        new Report.PluginRoleInfo("vocabulary", null, Read(() => (bool?)role.IsReady), null)))
                    .Concat(roles.Actions.Where(role => role.PluginId == id).Select(role =>
                        new Report.PluginRoleInfo("action", Report.Identifier(Read(() => role.ActionId)), null, null)))
                    .ToArray();
                return new Report.PluginInfo(
                    Report.Identifier(id),
                    Report.Identifier(plugin.Manifest.Version),
                    plugin.NetworkAccess.ToString(),
                    plugin.Enabled,
                    plugin.Active,
                    pluginRoles
                );
            })
            .OrderBy(plugin => plugin.Id, StringComparer.Ordinal)
            .ToArray()
        );

    internal static Report.ProfilesInfo SummarizeProfiles(IReadOnlyList<Profile> profiles, IReadOnlySet<string> trusted) =>
        new(
            profiles.Count,
            profiles.Count(profile => profile.IsEnabled),
            profiles.Where(profile => profile.IsEnabled).Select(profile =>
            {
                var (engineId, modelId) = SplitSelection(profile.TranscriptionModelOverride, trusted);
                return new Report.ProfileMetadata(
                    profile.ProcessNames.Count,
                    profile.UrlPatterns.Count,
                    profile.ContextMatchMode.ToString(),
                    !string.IsNullOrWhiteSpace(profile.HotkeyData),
                    profile.HotkeyBehavior.ToString(),
                    profile.StylePreset.ToString(),
                    Report.Identifier(profile.InputLanguage),
                    profile.InputLanguageHints.Count,
                    Report.Identifier(profile.SelectedTask),
                    Report.Identifier(profile.TranslationTarget),
                    profile.WhisperModeOverride,
                    profile.CleanupLevelOverride?.ToString(),
                    profile.DeveloperFormattingOverride,
                    engineId,
                    modelId,
                    !string.IsNullOrWhiteSpace(profile.PromptActionId)
                );
            }).ToArray()
        );

    internal static Report.PromptsInfo SummarizePrompts(
        IReadOnlyList<PromptAction> prompts,
        string? defaultLlmProvider,
        IReadOnlySet<string> trusted
    ) =>
        new(
            prompts.Count,
            prompts.Count(prompt => prompt.IsEnabled),
            prompts.Count(prompt => prompt.IsPreset),
            SplitSelection(defaultLlmProvider, trusted).Provider,
            prompts.Where(prompt => prompt.IsEnabled).Select(prompt =>
            {
                var (providerId, providerModelId) = SplitSelection(prompt.ProviderOverride, trusted);
                return new Report.PromptMetadata(
                    prompt.IsPreset,
                    prompt.IsManualOnly,
                    !string.IsNullOrWhiteSpace(prompt.HotkeyKey),
                    providerId,
                    string.IsNullOrWhiteSpace(prompt.ModelOverride)
                        ? providerModelId
                        : ModelId(
                            RawProvider(string.IsNullOrWhiteSpace(prompt.ProviderOverride)
                                ? defaultLlmProvider
                                : prompt.ProviderOverride),
                            prompt.ModelOverride,
                            trusted
                        ),
                    Report.Identifier(prompt.TargetActionPluginId)
                );
            }).ToArray()
        );

    /// <summary>Splits a <c>plugin:&lt;id&gt;:&lt;model&gt;</c> selection into filtered identifiers.</summary>
    private static (string? Provider, string? Model) SplitSelection(string? selection, IReadOnlySet<string> trusted)
    {
        if (string.IsNullOrWhiteSpace(selection))
        {
            return (null, null);
        }

        if (!ModelManagerService.IsPluginModel(selection))
        {
            return (Report.Identifier(selection), null);
        }

        try
        {
            var (provider, model) = ModelManagerService.ParsePluginModelId(selection);
            return (Report.Identifier(provider), ModelId(provider, model, trusted));
        }
        catch (ArgumentException)
        {
            return (Report.Omitted, null);
        }
    }

    private static string? RawProvider(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection) || !ModelManagerService.IsPluginModel(selection))
        {
            return selection;
        }

        try
        {
            return ModelManagerService.ParsePluginModelId(selection).PluginId;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // A plugin property that throws leaves its value out instead of failing every plugin.
    private static T? Read<T>(Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return default;
        }
    }

    internal sealed record Sources(
        Func<AppSettings> Settings,
        Func<IReadOnlySet<string>> TrustedModelProviders,
        Func<IReadOnlySet<string>, Report.ModelInfo> Model,
        Func<Report.ApiInfo> Api,
        Func<Report.AudioInfo> Audio,
        Func<IReadOnlySet<string>, Report.PluginsInfo> Plugins,
        Func<IReadOnlyList<Profile>> Profiles,
        Func<IReadOnlyList<PromptAction>> Prompts,
        Func<int> HistoryCount,
        Func<IReadOnlyList<DictionaryEntry>> Dictionary,
        Func<IReadOnlyList<Snippet>> Snippets,
        Func<int> ErrorLogCount,
        Func<Task<Report.LogInfo?>> Log
    );

    internal sealed record PluginState(PluginManifest Manifest, PluginNetworkAccess NetworkAccess, bool Enabled, bool Active);

    internal sealed record PluginRoles(
        IReadOnlyList<ITranscriptionEngineRole> Transcription,
        IReadOnlyList<ILlmProviderRole> Llm,
        IReadOnlyList<ITtsProviderPlugin> Tts,
        IReadOnlyList<IVocabularyRescorerPlugin> Vocabulary,
        IReadOnlyList<IActionPlugin> Actions
    );

    private sealed record Count(int Value);

    private sealed record DictionaryCounts(int Terms, int Corrections, int Enabled);

    private sealed record SnippetCounts(int Total, int Enabled);

    /// <summary>The store reported its data as unreadable or reset after corruption; its count is unknown.</summary>
    private sealed class StoreUnreadableException : IOException;
}
