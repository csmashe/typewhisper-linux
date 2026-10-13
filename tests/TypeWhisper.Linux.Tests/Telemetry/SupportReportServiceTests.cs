// ReSharper disable MethodHasAsyncOverload -- synchronous File.Read*/Write* is deliberate in these test assertions and fixtures.
using System.Text.Json;
using Moq;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.Diagnostics;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Telemetry;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Tests;
using Xunit;
using Report = TypeWhisper.Core.Services.Diagnostics.SupportReport;

namespace TypeWhisper.Linux.Tests.Telemetry;

public sealed class SupportReportServiceTests : IDisposable
{
    private const string SherpaId = "com.typewhisper.sherpa-onnx";
    private const string CtcId = "com.typewhisper.parakeet-ctc";

    private static readonly AudioInputDevice[] s_devices =
    [
        new(0, "Private Person's AirPods", 1, true, "Private Person's AirPods|1"),
        new(3, "USB Microphone", 2, false, "USB Microphone|2"),
        new(5, "USB Microphone", 2, false, "USB Microphone|4"),
    ];

    private static readonly IReadOnlySet<string> s_trusted =
        new HashSet<string>(StringComparer.Ordinal)
        {
            SherpaId, "sherpa-onnx", CtcId, "com.typewhisper.claude", "com.typewhisper.openrouter",
        };

    private readonly string _directory = TestPaths.CreateTempDirectory("TypeWhisper.SupportReportServiceTests");

    public void Dispose() => TestPaths.DeleteDirectory(_directory);

    [Fact]
    public void Audio_resolves_the_configured_device_by_its_stable_id_and_omits_names()
    {
        var settings = AppSettings.Default with { SelectedMicrophoneDevice = 3, SelectedMicrophoneDeviceId = "USB Microphone|4" };

        var audio = SupportReportService.SummarizeAudio(s_devices, true, false, settings);

        Assert.True(audio.HasConfiguredDevice);
        Assert.True(audio.ConfiguredDeviceAvailable);
        Assert.Equal([false, false, true], audio.InputDevices.Select(device => device.IsSelected));
        Assert.Equal([1, 2, 3], audio.InputDevices.Select(device => device.Ordinal));
        Assert.True(audio.InputDevices[0].IsDefault);
        var json = (Empty() with { Audio = audio }).ToJson();
        Assert.DoesNotContain("Private", json, StringComparison.Ordinal);
        Assert.DoesNotContain("USB", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Audio_reports_a_disconnected_device_without_falling_back_to_its_index()
    {
        var settings = AppSettings.Default with { SelectedMicrophoneDevice = 3, SelectedMicrophoneDeviceId = "Unplugged|1" };

        var audio = SupportReportService.SummarizeAudio(s_devices, true, false, settings);

        Assert.True(audio.HasConfiguredDevice);
        Assert.False(audio.ConfiguredDeviceAvailable);
        Assert.DoesNotContain(audio.InputDevices, device => device.IsSelected);
    }

    [Theory]
    [InlineData(AppSettings.FollowSystemDefaultMicrophoneId, true, false)]
    [InlineData(null, false, false)]
    public void Audio_without_a_pinned_device_selects_the_system_default(string? deviceId, bool follows, bool configured)
    {
        var settings = AppSettings.Default with { SelectedMicrophoneDevice = null, SelectedMicrophoneDeviceId = deviceId };

        var audio = SupportReportService.SummarizeAudio(s_devices, false, true, settings);

        Assert.Equal(follows, audio.FollowsSystemDefault);
        Assert.Equal(configured, audio.HasConfiguredDevice);
        Assert.True(audio.ConfiguredDeviceAvailable);
        Assert.False(audio.NativeAudioAvailable);
        Assert.True(audio.Recording);
        Assert.Equal([true, false, false], audio.InputDevices.Select(device => device.IsSelected));
    }

    [Fact]
    public void Profiles_keep_technical_metadata_without_user_content()
    {
        var profile = new Profile
        {
            Id = "private-profile-id",
            Name = "private-profile-name",
            ProcessNames = ["private-app"],
            UrlPatterns = ["https://private.example", "private.example/*"],
            HotkeyData = "private-shortcut",
            InputLanguage = "de",
            InputLanguageHints = ["en"],
            TranslationTarget = "en",
            TranscriptionModelOverride = "plugin:" + SherpaId + ":parakeet-tdt-0.6b",
            PromptActionId = "private-prompt-id",
            MemorySourceId = "com.typewhisper.file-memory",
            StylePreset = ProfileStylePreset.Clean,
            CleanupLevelOverride = CleanupLevel.Medium,
        };

        var profiles = SupportReportService.SummarizeProfiles([profile, profile with { IsEnabled = false }], s_trusted);

        Assert.Equal(2, profiles.Total);
        Assert.Equal(1, profiles.Enabled);
        var metadata = Assert.Single(profiles.EnabledProfiles);
        Assert.Equal(1, metadata.AppBindingCount);
        Assert.Equal(2, metadata.UrlBindingCount);
        Assert.True(metadata.HasHotkey);
        Assert.True(metadata.HasPrompt);
        Assert.Equal(SherpaId, metadata.TranscriptionEngineId);
        Assert.Equal("parakeet-tdt-0.6b", metadata.TranscriptionModelId);
        Assert.Equal("de", metadata.InputLanguage);
        Assert.Equal("Clean", metadata.StylePreset);
        Assert.Equal("com.typewhisper.file-memory", metadata.MemorySourceId);
        Assert.DoesNotContain("private", (Empty() with { Profiles = profiles }).ToJson(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prompts_keep_provider_ids_and_omit_instructions_and_path_like_models()
    {
        var prompt = new PromptAction
        {
            Id = "private-prompt-id",
            Name = "private-prompt-name",
            SystemPrompt = "private instructions",
            ProviderOverride = "plugin:com.typewhisper.openrouter:private-org/private-model",
            TargetActionPluginId = "com.typewhisper.webhook",
            HotkeyKey = "private-key",
        };

        var prompts = SupportReportService.SummarizePrompts(
            [prompt, prompt with { IsEnabled = false, IsPreset = true }],
            "plugin:com.typewhisper.claude:claude-sonnet-5",
            s_trusted
        );

        Assert.Equal(2, prompts.Total);
        Assert.Equal(1, prompts.Presets);
        Assert.Equal("com.typewhisper.claude", prompts.DefaultLlmProviderId);
        var metadata = Assert.Single(prompts.EnabledPrompts);
        Assert.Equal("com.typewhisper.openrouter", metadata.ProviderId);
        Assert.Equal(Report.Omitted, metadata.ModelId);
        Assert.Equal("com.typewhisper.webhook", metadata.ActionPluginId);
        Assert.True(metadata.HasHotkey);
        Assert.DoesNotContain("private", (Empty() with { Prompts = prompts }).ToJson(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plugins_report_ctc_readiness_apart_from_sherpa_and_survive_a_throwing_role()
    {
        var sherpa = new Mock<ITranscriptionEngineRole>();
        sherpa.SetupGet(role => role.PluginId).Returns(SherpaId);
        sherpa.SetupGet(role => role.IsConfigured).Returns(true);
        sherpa.SetupGet(role => role.SelectedModelId).Returns("parakeet-ultra-0.6b");
        var ctc = new Mock<IVocabularyRescorerPlugin>();
        ctc.SetupGet(role => role.PluginId).Returns(CtcId);
        ctc.SetupGet(role => role.IsReady).Returns(false);
        var broken = new Mock<ILlmProviderRole>();
        broken.SetupGet(role => role.PluginId).Returns("com.example.broken");
        broken.SetupGet(role => role.IsAvailable).Throws(new InvalidOperationException("/home/private"));

        var plugins = SupportReportService.SummarizePlugins(
            [
                State(SherpaId, enabled: true, active: true),
                State(CtcId, enabled: true, active: true),
                State("com.example.broken", enabled: true, active: false),
                State("../private", enabled: false, active: false),
            ],
            new SupportReportService.PluginRoles([sherpa.Object], [broken.Object], [], [ctc.Object], []),
            loadFailures: 2,
            s_trusted
        );

        Assert.Equal(2, plugins.LoadFailures);
        var sherpaInfo = plugins.Installed.Single(plugin => plugin.Id == SherpaId);
        var transcription = Assert.Single(sherpaInfo.Roles);
        Assert.Equal(("transcription", SherpaId, true, "parakeet-ultra-0.6b"),
            (transcription.Kind, transcription.Id, transcription.Ready, transcription.SelectedModelId));
        var ctcRole = Assert.Single(plugins.Installed.Single(plugin => plugin.Id == CtcId).Roles);
        Assert.Equal(("vocabulary", (bool?)false), (ctcRole.Kind, ctcRole.Ready));
        var brokenInfo = plugins.Installed.Single(plugin => plugin.Id == "com.example.broken");
        Assert.True(brokenInfo.Enabled);
        Assert.False(brokenInfo.Active);
        Assert.Null(Assert.Single(brokenInfo.Roles).Ready);
        Assert.Contains(plugins.Installed, plugin => plugin.Id == Report.Omitted);
        Assert.DoesNotContain("private", (Empty() with { Plugins = plugins }).ToJson(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Model_ids_of_user_controlled_or_unknown_providers_are_omitted()
    {
        const string server = "com.typewhisper.openai-compatible";
        var openAiCompatible = new Mock<ITranscriptionEngineRole>();
        openAiCompatible.SetupGet(role => role.PluginId).Returns(server);
        openAiCompatible.SetupGet(role => role.SelectedModelId).Returns("patient-alice.gguf");
        var profile = new Profile
        {
            Id = "p", Name = "p", TranscriptionModelOverride = "plugin:" + server + ":acme-confidential-project",
        };
        var prompts = new[]
        {
            new PromptAction { Id = "a", Name = "a", SystemPrompt = "a", ProviderOverride = "plugin:" + server + ":private-deployment" },
            new PromptAction { Id = "b", Name = "b", SystemPrompt = "b", ModelOverride = "private-default-model" },
            new PromptAction { Id = "c", Name = "c", SystemPrompt = "c", ModelOverride = "claude-sonnet-5" },
        };

        var plugins = SupportReportService.SummarizePlugins(
            [State(server, enabled: true, active: true)],
            new SupportReportService.PluginRoles([openAiCompatible.Object], [], [], [], []),
            0,
            s_trusted
        );
        var profiles = SupportReportService.SummarizeProfiles([profile], s_trusted);
        var summary = SupportReportService.SummarizePrompts(prompts, "plugin:" + server + ":private-llm", s_trusted);

        Assert.Equal(Report.Omitted, Assert.Single(Assert.Single(plugins.Installed).Roles).SelectedModelId);
        Assert.Equal(server, Assert.Single(profiles.EnabledProfiles).TranscriptionEngineId);
        Assert.Equal(Report.Omitted, profiles.EnabledProfiles[0].TranscriptionModelId);
        Assert.Equal([Report.Omitted, Report.Omitted, Report.Omitted], summary.EnabledPrompts.Select(prompt => prompt.ModelId));
        Assert.Equal(server, summary.DefaultLlmProviderId);
        var json = (Empty() with { Plugins = plugins, Profiles = profiles, Prompts = summary }).ToJson();
        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alice", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("confidential", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "claude-sonnet-5",
            SupportReportService.SummarizePrompts([prompts[2]], "plugin:com.typewhisper.claude:x", s_trusted)
                .EnabledPrompts[0].ModelId
        );
    }

    [Theory]
    [InlineData("com.typewhisper.claude", "ft:gpt-4o:acme:confidential:abc", true)]
    [InlineData("com.typewhisper.claude", "claude-sonnet-5", false)]
    public void Fine_tuned_models_are_omitted_even_for_trusted_providers(string provider, string model, bool omitted) =>
        Assert.Equal(omitted ? Report.Omitted : model, SupportReportService.ModelId(provider, model, s_trusted));

    [Fact]
    public void Log_entries_keep_model_ids_only_from_trusted_providers()
    {
        var log = new Report.LogInfo(true, 7, [
            Line("sherpa-onnx", "parakeet-tdt-0.6b"),
            Line("openai-compatible", "patient-alice.gguf"),
            Line(null, "acme-confidential"),
            Line(null, "plugin:" + SherpaId + ":parakeet-ultra-0.6b"),
            Line(null, "plugin:com.typewhisper.openai-compatible:acme-confidential"),
            new DiagnosticLogLine(DateTimeOffset.UtcNow, "app.start"),
        ]);

        var redacted = SupportReportService.RedactLogModels(log, s_trusted);

        Assert.Equal(
            ["parakeet-tdt-0.6b", "custom", "custom", "plugin:" + SherpaId + ":parakeet-ultra-0.6b", "custom", null],
            redacted.Entries.Select(entry => entry.Data?.GetValueOrDefault("engine.model"))
        );
        Assert.Equal("Hybrid", redacted.Entries[1].Data!["mode"]);
        return;

        static DiagnosticLogLine Line(string? provider, string model)
        {
            var data = new Dictionary<string, string> { ["engine.model"] = model, ["mode"] = "Hybrid" };
            if (provider is not null)
            {
                data["engine.provider"] = provider;
            }

            return new DiagnosticLogLine(DateTimeOffset.UtcNow, "dictation.session.end", Data: data);
        }
    }

    [Fact]
    public void Session_reports_type_and_filtered_desktops()
    {
        var session = SupportReportService.SummarizeSession("wayland-0", "wayland", "ubuntu:GNOME:/home/private", ":0", false);

        Assert.Equal("wayland", session.Type);
        Assert.Equal(["ubuntu", "GNOME", Report.Omitted], session.Desktops);
        Assert.True(session.HasWaylandDisplay);
        Assert.True(session.HasX11Display);
        Assert.False(session.EvdevHotkeysEnabled);
        Assert.Equal("x11", SupportReportService.SummarizeSession(null, "x11", null, ":0", true).Type);
        Assert.Equal("unknown", SupportReportService.SummarizeSession(null, null, null, null, true).Type);
    }

    [Fact]
    public void Os_release_values_are_read_unquoted()
    {
        var path = Path.Join(_directory, "os-release");
        File.WriteAllLines(path, ["NAME=\"Fedora Linux\"", "ID=fedora", "VERSION_ID=42", "# comment"]);

        var values = SupportReportService.ReadOsRelease(path);

        Assert.Equal("fedora", values["ID"]);
        Assert.Equal("42", values["VERSION_ID"]);
        Assert.Equal("Fedora Linux", values["NAME"]);
        Assert.Empty(SupportReportService.ReadOsRelease(Path.Join(_directory, "missing")));
    }

    [Fact]
    public async Task Failing_collectors_leave_other_sections_and_no_content()
    {
        var settings = AppSettings.Default with
        {
            SelectedModelId = "plugin:" + SherpaId + ":parakeet-tdt-0.6b",
            ApiServerBearerToken = "private-token",
            LocalModelStoragePath = "/home/private/models",
            DefaultLlmProvider = "plugin:com.typewhisper.claude:claude-sonnet-5",
        };
        var service = new SupportReportService(new SupportReportService.Sources(
            () => settings,
            () => s_trusted,
            _ => throw new InvalidOperationException("private model failure"),
            () => new Report.ApiInfo(true, false, 9876, true),
            () => SupportReportService.SummarizeAudio(s_devices, true, false, settings),
            _ => throw new IOException("/home/private/plugins"),
            () => [],
            () => [new PromptAction { Id = "private", Name = "private", SystemPrompt = "private" }],
            () => 12,
            () => [
                new DictionaryEntry { Id = "private-1", EntryType = DictionaryEntryType.Term, Original = "private term" },
                new DictionaryEntry
                {
                    Id = "private-2", EntryType = DictionaryEntryType.Correction, Original = "private", Replacement = "private",
                    IsEnabled = false,
                },
            ],
            () => throw new UnauthorizedAccessException("/home/private/snippets.json"),
            () => 3,
            () => Task.FromResult<Report.LogInfo?>(new Report.LogInfo(false, 7, [new DiagnosticLogLine(DateTimeOffset.UtcNow, "app.start")]))
        ));

        var report = await service.CaptureAsync();
        var json = report.ToJson();

        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["model", "plugins", "snippetCounts"], report.CollectionErrors.Select(error => error.Section).Order());
        Assert.Null(report.Model);
        Assert.NotNull(report.Api);
        Assert.NotNull(report.Settings);
        Assert.True(report.Settings!.CustomModelStorage);
        Assert.Equal(1, report.Prompts!.Total);
        Assert.Equal((12, 1, 1, 1, null, 3),
            (report.Counts!.HistoryRecords, report.Counts.DictionaryTerms, report.Counts.DictionaryCorrections,
                report.Counts.EnabledDictionaryEntries, report.Counts.Snippets, report.Counts.ErrorLogEntries));
        using var document = JsonDocument.Parse(json);
        Assert.Empty(document.RootElement.GetProperty("log").GetProperty("entries").EnumerateArray());
        Assert.Equal("linux", document.RootElement.GetProperty("platform").GetString());
    }

    [Fact]
    public async Task A_malformed_entry_fails_only_its_own_count()
    {
        var service = new SupportReportService(new SupportReportService.Sources(
            () => AppSettings.Default,
            () => s_trusted,
            _ => throw new InvalidOperationException(),
            () => new Report.ApiInfo(false, false, 9876, false),
            () => SupportReportService.SummarizeAudio([], true, false, AppSettings.Default),
            _ => new Report.PluginsInfo(0, []),
            () => [],
            () => [],
            () => 0,
            () => [null!],
            () => [new Snippet { Id = "s", Trigger = "t", Replacement = "r" }, null!],
            () => 0,
            () => Task.FromResult<Report.LogInfo?>(null)
        ));

        var report = await service.CaptureAsync();

        Assert.Contains(report.CollectionErrors, error => error.Section == "dictionaryCounts");
        Assert.Contains(report.CollectionErrors, error => error.Section == "snippetCounts");
        Assert.Equal((0, null, null, 0),
            (report.Counts!.HistoryRecords, report.Counts.DictionaryTerms, report.Counts.Snippets, report.Counts.ErrorLogEntries));
    }

    [Fact]
    public async Task Log_capture_ignores_a_leftover_file_while_disabled_and_flushes_while_enabled()
    {
        var path = Path.Join(_directory, "diagnostics.jsonl");
        var settings = new FakeSettingsService(AppSettings.Default with { DiagnosticLogEnabled = true });
        using var log = new LocalDiagnosticLogService(settings, path, TimeProvider.System);
        log.Start();
        log.CaptureException(new InvalidOperationException("private"), "transcription");

        var enabled = await log.CaptureForReportAsync();

        Assert.True(enabled.Enabled);
        Assert.Contains(enabled.Entries, entry => entry.Error == typeof(InvalidOperationException).FullName);

        File.WriteAllText(path, JsonSerializer.Serialize(new DiagnosticLogLine(DateTimeOffset.UtcNow, "app.start")) + "\n");
        using var disabled = new LocalDiagnosticLogService(
            new FakeSettingsService(AppSettings.Default with { DiagnosticLogEnabled = false }), path, TimeProvider.System);
        var report = await disabled.CaptureForReportAsync();

        Assert.False(report.Enabled);
        Assert.Empty(report.Entries);
    }

    private static SupportReportService.PluginState State(string id, bool enabled, bool active) =>
        new(
            new PluginManifest { Id = id, Name = "private name", Version = "1.0.0", AssemblyName = "x.dll", PluginClass = "X" },
            PluginNetworkAccess.Local,
            enabled,
            active
        );

    private static Report Empty() =>
        new(DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null, []);
}
