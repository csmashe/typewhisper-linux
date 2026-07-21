// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Globalization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.SupertonicTts;

public sealed class SupertonicTtsPlugin : ITtsProviderPlugin, IPluginSettingsProvider, IPluginSettingsActivity, IPluginLocalizationAware
{
    internal const string LicenseAcceptedSettingName = "licenseAccepted";
    internal const string SelectedVoiceSettingName = "selectedVoice";
    internal const string SpeedSettingName = "speed";
    internal const string DenoisingStepsSettingName = "denoisingSteps";
    internal const string DefaultVoiceId = "M1";
    internal const double DefaultSpeed = 1.05;
    internal const int DefaultDenoisingSteps = 8;
    internal const double MinSpeed = 0.9;
    internal const double MaxSpeed = 1.5;
    internal const int MinDenoisingSteps = 1;
    internal const int MaxDenoisingSteps = 16;

    private static readonly IReadOnlyList<PluginVoiceInfo> s_voices =
    [
        new("M1", "M1"),
        new("M2", "M2"),
        new("M3", "M3"),
        new("M4", "M4"),
        new("M5", "M5"),
        new("F1", "F1"),
        new("F2", "F2"),
        new("F3", "F3"),
        new("F4", "F4"),
        new("F5", "F5"),
    ];

    private readonly ISupertonicAssetManager? _injectedAssetManager;
    private readonly Func<string, ISupertonicSynthesizer> _synthesizerFactory;
    private readonly Func<float[], int, ITtsPlaybackSession> _playbackFactory;
    private readonly SemaphoreSlim _synthesisLock = new(1, 1);
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private ISupertonicAssetManager? _assetManager;
    private ISupertonicSynthesizer? _synthesizer;
    private IPluginHostServices? _host;
    private string _selectedVoiceId = DefaultVoiceId;

    // Progress<T> posts its callbacks asynchronously, so a late download tick can
    // race the post-download clear. The lock + done-latch make the clear authoritative:
    // once CompleteActivity runs, late progress reports are dropped.
    private readonly Lock _activityLock = new();
    private bool _settingsActivityDone;
    private bool _disposed;

    public SupertonicTtsPlugin()
        : this(
            assetManager: null,
            synthesizerFactory: assetRoot => new SupertonicOnnxSynthesizer(assetRoot),
            playbackFactory: (samples, sampleRate) => SupertonicTtsPlaybackSession.Create(samples, sampleRate),
            useNullableAssetManagerOverload: true)
    {
    }

    internal SupertonicTtsPlugin(
        ISupertonicAssetManager assetManager,
        Func<string, ISupertonicSynthesizer> synthesizerFactory,
        Func<float[], int, ITtsPlaybackSession>? playbackFactory = null)
        : this(assetManager, synthesizerFactory, playbackFactory, useNullableAssetManagerOverload: true)
    {
    }

    private SupertonicTtsPlugin(
        ISupertonicAssetManager? assetManager,
        Func<string, ISupertonicSynthesizer> synthesizerFactory,
        Func<float[], int, ITtsPlaybackSession>? playbackFactory,
        // ReSharper disable once UnusedParameter.Local -- disambiguates the constructor overload; required by the signature even though unused in the body.
        bool useNullableAssetManagerOverload)
    {
        _injectedAssetManager = assetManager;
        _assetManager = assetManager;
        _synthesizerFactory = synthesizerFactory;
        _playbackFactory = playbackFactory
            ?? ((samples, sampleRate) => SupertonicTtsPlaybackSession.Create(samples, sampleRate));
    }

    public string PluginId => "com.typewhisper.supertonic-tts";
    public string PluginName => "Supertonic TTS";
    public string PluginVersion => "1.0.0";
    public string ProviderId => "supertonic-tts";
    public string ProviderDisplayName => "Supertonic TTS";
    public bool IsConfigured => _assetManager?.AreAssetsReady ?? false;
    public IReadOnlyList<PluginVoiceInfo> AvailableVoices => s_voices;
    // ReSharper disable once ReturnTypeCanBeNotNullable -- matches the interface contract, which declares this member nullable.
    public string? SelectedVoiceId => _selectedVoiceId;
    internal double Speed { get; private set; } = DefaultSpeed;
    internal int DenoisingSteps { get; private set; } = DefaultDenoisingSteps;
    internal bool HasAcceptedModelLicense { get; private set; }

    internal bool AreAssetsReady => IsConfigured;
    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    // ReSharper disable once ReturnTypeCanBeNotNullable -- matches the interface contract, which declares this member nullable.
    public string? SettingsSummary
    {
        get
        {
            var status = IsConfigured
                ? L("Settings.StatusReady")
                : L("Settings.StatusDownloadRequired");
            return L(
                "Settings.Summary",
                _selectedVoiceId,
                Speed.ToString("0.##", CultureInfo.InvariantCulture),
                DenoisingSteps,
                status
            );
        }
    }

    // IPluginSettingsActivity — surfaces the on-demand model download progress
    // in the host's generic settings UI (upstream showed it via the WPF
    // XaiSettingsView progress bar).
    public double? SettingsProgress { get; private set; }

    public event Action<string?>? SettingsActivityChanged;

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _assetManager = _injectedAssetManager
            ?? new SupertonicAssetManager(Path.Join(host.PluginAssetDirectory, "Models", SupertonicPaths.ModelDirectoryName));
        _selectedVoiceId = NormalizeVoiceId(host.GetSetting<string>(SelectedVoiceSettingName));
        Speed = NormalizeSpeed(host.GetSetting<double?>(SpeedSettingName) ?? DefaultSpeed);
        DenoisingSteps = NormalizeDenoisingSteps(host.GetSetting<int?>(DenoisingStepsSettingName) ?? DefaultDenoisingSteps);
        HasAcceptedModelLicense = host.GetSetting<bool?>(LicenseAcceptedSettingName).GetValueOrDefault();
        PersistSettings();
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
        return Task.CompletedTask;
    }

    public async Task DeactivateAsync()
    {
        // Wait for any in-flight synthesis to finish before tearing down the
        // ONNX sessions — disposing them mid-inference crashes the native
        // runtime.
        await ResetSynthesizerAsync();
        _host = null;
    }

    public void SelectVoice(string? voiceId)
    {
        _selectedVoiceId = NormalizeVoiceId(voiceId);
        _host?.SetSetting(SelectedVoiceSettingName, _selectedVoiceId);
    }

    public async Task<ITtsPlaybackSession> SpeakAsync(TtsSpeakRequest request, CancellationToken ct)
    {
        var text = request.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return SupertonicInactiveTtsPlaybackSession.Instance;

        if (_assetManager?.AreAssetsReady != true)
            throw new InvalidOperationException("Supertonic 3 assets are not downloaded. Open plugin settings to download them.");

        await _synthesisLock.WaitAsync(ct);
        try
        {
            var synthesizer = _synthesizer ??= _synthesizerFactory(_assetManager.AssetRoot);
            var synthesis = synthesizer.Synthesize(
                new SupertonicSynthesisRequest(
                    text,
                    NormalizeLanguage(request.Language),
                    SupertonicPaths.VoiceStylePath(_assetManager.AssetRoot, _selectedVoiceId),
                    DenoisingSteps,
                    Speed),
                ct);

            return synthesis.Samples.Length == 0
                ? SupertonicInactiveTtsPlaybackSession.Instance
                : _playbackFactory(synthesis.Samples, synthesis.SampleRate);
        }
        finally
        {
            _synthesisLock.Release();
        }
    }

    // IPluginSettingsProvider
    //
    // Upstream exposed these via the WPF SupertonicSettingsView UserControl;
    // the fork renders settings generically from the metadata below. The fork's
    // IPluginSettingsProvider has no explicit "download" button, so the
    // on-demand model download is triggered from ValidateAsync (the host's
    // key-test / validation entry point) once the OpenRAIL-M license box is
    // ticked, and reports progress through IPluginSettingsActivity.

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                Key: LicenseAcceptedSettingName,
                Label: L("Settings.AcceptLicense"),
                Description: L("Settings.Description"),
                Kind: PluginSettingKind.Boolean
            ),
            new(
                Key: SelectedVoiceSettingName,
                Label: L("Settings.Voice"),
                Description: L("Settings.VoiceDescription"),
                Options: s_voices
                    .Select(voice => new PluginSettingOption(voice.Id, voice.DisplayName))
                    .ToList()
            ),
            new(
                Key: SpeedSettingName,
                Label: L("Settings.Speed"),
                Placeholder: $"{MinSpeed.ToString("0.##", CultureInfo.InvariantCulture)} – {MaxSpeed.ToString("0.##", CultureInfo.InvariantCulture)}",
                Description: L(
                    "Settings.SpeedDescription",
                    MinSpeed.ToString("0.##", CultureInfo.InvariantCulture),
                    MaxSpeed.ToString("0.##", CultureInfo.InvariantCulture)
                ),
                Kind: PluginSettingKind.Text
            ),
            new(
                Key: DenoisingStepsSettingName,
                Label: L("Settings.Quality"),
                Placeholder: $"{MinDenoisingSteps} – {MaxDenoisingSteps}",
                Description: L(
                    "Settings.QualityDescription",
                    MinDenoisingSteps,
                    MaxDenoisingSteps
                ),
                Kind: PluginSettingKind.Text
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                LicenseAcceptedSettingName => HasAcceptedModelLicense ? "true" : "false",
                SelectedVoiceSettingName => _selectedVoiceId,
                SpeedSettingName => Speed.ToString("0.##", CultureInfo.InvariantCulture),
                DenoisingStepsSettingName => DenoisingSteps.ToString(CultureInfo.InvariantCulture),
                _ => null,
            }
        );

    public Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        switch (key)
        {
            case LicenseAcceptedSettingName:
                SetLicenseAccepted(ParseBool(value));
                break;
            case SelectedVoiceSettingName:
                if (!string.IsNullOrWhiteSpace(value))
                    SelectVoice(value);
                break;
            case SpeedSettingName:
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
                    SetSpeed(speed);
                break;
            case DenoisingStepsSettingName:
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var steps))
                    SetDenoisingSteps(steps);
                break;
        }

        return Task.CompletedTask;
    }

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        if (IsConfigured)
            return new PluginSettingsValidationResult(true, L("Settings.Ready"));

        if (!HasAcceptedModelLicense)
            return new PluginSettingsValidationResult(false, L("Settings.AcceptLicense"));

        try
        {
            lock (_activityLock)
                _settingsActivityDone = false;

            ReportActivity(L("Settings.Downloading"), 0.0);
            var progress = new Progress<double>(value =>
                ReportActivity(L("Settings.Downloading"), Math.Clamp(value, 0.0, 1.0)));
            await DownloadAssetsAsync(progress, ct);
            CompleteActivity();
            return new PluginSettingsValidationResult(true, L("Settings.DownloadComplete"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            CompleteActivity();
            return new PluginSettingsValidationResult(false, L("Settings.DownloadCancelled"));
        }
        catch (Exception ex) when (ex is HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException
            or InvalidOperationException
            or OperationCanceledException)
        {
            CompleteActivity();
            _host?.Log(PluginLogLevel.Warning, $"Supertonic asset download failed: {ex.Message}");
            return new PluginSettingsValidationResult(false, L("Settings.Error", ex.Message));
        }
    }

    internal void SetLicenseAccepted(bool accepted)
    {
        HasAcceptedModelLicense = accepted;
        _host?.SetSetting(LicenseAcceptedSettingName, accepted);
    }

    internal void SetSpeed(double speed)
    {
        Speed = NormalizeSpeed(speed);
        _host?.SetSetting(SpeedSettingName, Speed);
    }

    internal void SetDenoisingSteps(int steps)
    {
        DenoisingSteps = NormalizeDenoisingSteps(steps);
        _host?.SetSetting(DenoisingStepsSettingName, DenoisingSteps);
    }

    internal async Task DownloadAssetsAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SupertonicTtsPlugin));

        if (!HasAcceptedModelLicense)
            throw new InvalidOperationException("The Supertonic 3 OpenRAIL-M license must be accepted before downloading model assets.");

        if (_assetManager is null)
            throw new InvalidOperationException("Plugin is not activated.");

        // Serialize downloads: the metadata-driven settings UI has no
        // "downloading" button state (upstream's WPF view did), so a repeated
        // Validate click could otherwise run two downloads racing on the same
        // temp files.
        await _downloadLock.WaitAsync(ct);
        try
        {
            if (_assetManager.AreAssetsReady)
                return;

            await _assetManager.DownloadMissingAssetsAsync(progress, ct);
            await ResetSynthesizerAsync();
            _host?.NotifyCapabilitiesChanged();
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    // Disposes the ONNX synthesizer under the synthesis lock so it never tears
    // down InferenceSession instances while SpeakAsync is mid-inference.
    private async Task ResetSynthesizerAsync()
    {
        await _synthesisLock.WaitAsync();
        try
        {
            _synthesizer?.Dispose();
            _synthesizer = null;
        }
        finally
        {
            _synthesisLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        // Set _disposed first so any DownloadAssetsAsync still waiting for the
        // download lock bails out before touching the asset manager. Then take
        // the download lock to wait for an already-in-flight download to finish
        // — disposing the asset manager while a download is mid-write would
        // corrupt the staging files and crash the next launch.
        _disposed = true;

        _downloadLock.Wait();
        try
        {
            // Block teardown until any in-flight synthesis releases the lock so
            // the ONNX sessions are never disposed mid-inference.
            _synthesisLock.Wait();
            try
            {
                _synthesizer?.Dispose();
                _synthesizer = null;
            }
            finally
            {
                _synthesisLock.Release();
            }

            if (_injectedAssetManager is null && _assetManager is IDisposable disposableAssets)
                disposableAssets.Dispose();
            _synthesisLock.Dispose();
        }
        finally
        {
            _downloadLock.Release();
        }

        _downloadLock.Dispose();
    }

    internal static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return "en";

        var normalized = language.Trim().ToLowerInvariant();
        var separator = normalized.IndexOfAny(['-', '_']);
        if (separator > 0)
            normalized = normalized[..separator];

        return SupertonicTextProcessor.SupportedLanguages.Contains(normalized)
            ? normalized
            : "en";
    }

    internal static double NormalizeSpeed(double speed)
    {
        if (double.IsNaN(speed) || double.IsInfinity(speed))
            return DefaultSpeed;
        return Math.Round(Math.Max(MinSpeed, Math.Min(MaxSpeed, speed)), 2);
    }

    internal static int NormalizeDenoisingSteps(int steps) =>
        Math.Max(MinDenoisingSteps, Math.Min(MaxDenoisingSteps, steps));

    private static bool ParseBool(string? value) =>
        bool.TryParse(value, out var parsed) && parsed;

    private static string NormalizeVoiceId(string? voiceId) =>
        !string.IsNullOrWhiteSpace(voiceId)
        && s_voices.Any(voice => string.Equals(voice.Id, voiceId.Trim(), StringComparison.OrdinalIgnoreCase))
            ? s_voices.First(voice => string.Equals(voice.Id, voiceId.Trim(), StringComparison.OrdinalIgnoreCase)).Id
            : DefaultVoiceId;

    private void ReportActivity(string? message, double? progress)
    {
        lock (_activityLock)
        {
            // Drop late Progress<T> callbacks that arrive after the activity has
            // completed — they must not resurrect a value we already cleared.
            // A clear (null progress) is always allowed through.
            if (_settingsActivityDone && progress is not null)
                return;
            SettingsProgress = progress;
        }

        SettingsActivityChanged?.Invoke(message);
    }

    // Marks the settings activity finished and clears progress so subsequent
    // late progress callbacks are ignored (see ReportActivity).
    private void CompleteActivity()
    {
        lock (_activityLock)
        {
            _settingsActivityDone = true;
            SettingsProgress = null;
        }

        SettingsActivityChanged?.Invoke(null);
    }

    private string L(string key) => Loc?.GetString(key) ?? key;

    private string L(string key, params object[] args) =>
        Loc is { } loc ? loc.GetString(key, args) : string.Format(CultureInfo.CurrentCulture, key, args);

    private void PersistSettings()
    {
        if (_host is null)
            return;

        _host.SetSetting(SelectedVoiceSettingName, _selectedVoiceId);
        _host.SetSetting(SpeedSettingName, Speed);
        _host.SetSetting(DenoisingStepsSettingName, DenoisingSteps);
    }
}
