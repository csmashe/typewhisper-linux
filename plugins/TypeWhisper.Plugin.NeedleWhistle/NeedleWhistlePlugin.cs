using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TypeWhisper.Plugins.Shared.Net;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.Plugin.NeedleWhistle;

public sealed class NeedleWhistlePlugin
    : ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
        IPluginLocalizationAware
{
    private const string ModelId = "whistle";
    private const string RuntimeRevision = "93e88ede867e4311c39151285e74df513e4b835b";
    private const string ModelRevision = "ca5287601bef25af26dcf2e1b2bdc0843a7c19e5";
    private const string RuntimeUrl =
        $"https://huggingface.co/Cactus-Compute/needle3/resolve/{RuntimeRevision}/linux-x86_64/needle";
    private const string ModelUrl =
        $"https://huggingface.co/Cactus-Compute/whistle/resolve/{ModelRevision}/whistle.cact";
    private const long RuntimeSize = 1_548_480;
    private const string RuntimeSha256 = "3531889c4d677acc47905b21c79741b52fd9af2c54ba177f54f83c0bc12e84c7";
    private const long ModelSize = 16_919_407;
    private const string ModelSha256 = "b6e02f048568ac5d01a2042556c658061e699acbc0aa2a1439f52f3d461dffeb";
    private const long MaximumOutputBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan s_processTimeout = TimeSpan.FromMinutes(5);
    private static readonly IReadOnlyList<string> s_supportedLanguages = ["en", "de", "fr", "es", "it", "nl", "pl"];
    private static readonly DictionaryTermsBudget s_dictionaryTermsBudget = new(
        MaxTerms: 32,
        MaxCharsPerTerm: 128,
        MaxWordsPerTerm: 6,
        MaxTotalChars: 600
    );
    private static readonly AssetDefinition[] s_assets =
    [
        new("runtime/needle", RuntimeUrl, RuntimeSize, RuntimeSha256, IsExecutable: true),
        new("Models/whistle/whistle.cact", ModelUrl, ModelSize, ModelSha256, IsExecutable: false),
    ];

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _assetGate = new(1, 1);
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private IPluginHostServices? _host;
    private IPluginProcessSupervisor? _processes;
    private IPluginLocalization? _injectedLocalization;
    private string? _selectedModelId;
    private bool _platformSupported;
    private bool _disposed;

    public NeedleWhistlePlugin()
        : this(new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
        {
            Timeout = TimeSpan.FromHours(2),
        }) { }

    internal NeedleWhistlePlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string PluginId => "com.typewhisper.needle-whistle";
    public string PluginName => Loc.GetString("Manifest.Name");
    public string PluginVersion => PluginBuildInfo.Version;
    public string ProviderId => "needle-whistle";
    public string ProviderDisplayName => Loc.GetString("Manifest.Name");
    public bool IsConfigured => _platformSupported && _processes is not null;
    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
    [
        new PluginModelInfo(ModelId, "Whistle")
        {
            SizeDescription = "~18 MB",
            EstimatedSizeMB = 18,
            IsRecommended = true,
            LanguageCount = 7,
            SupportsTranslation = false,
        },
    ];
    public string? SelectedModelId => _selectedModelId;
    public bool SupportsTranslation => false;
    public bool SupportsModelDownload => true;
    public bool SupportsStructuredDictionaryTerms => true;
    public DictionaryTermsBudget? DictionaryTermsBudget => s_dictionaryTermsBudget;
    public IReadOnlyList<string> SupportedLanguages => s_supportedLanguages;
    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;
    public TranscriptionAccelerationPreference AccelerationPreference => TranscriptionAccelerationPreference.Cpu;
    public IReadOnlyList<TranscriptionAccelerationBackend> SupportedAccelerationBackends =>
        [TranscriptionAccelerationBackend.Cpu];
    public TranscriptionAccelerationStatus AccelerationStatus { get; } =
        new(TranscriptionAccelerationBackend.Cpu, "Using CPU");

    public void SetLocalization(IPluginLocalization localization) => _injectedLocalization = localization;

    public Task ActivateAsync(IPluginHostServices host)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _host = host;
        _platformSupported = OperatingSystem.IsLinux()
            && RuntimeInformation.ProcessArchitecture == Architecture.X64;
        _selectedModelId = host.GetSetting<string>("selectedModel") ?? ModelId;
        if (_selectedModelId != ModelId)
            _selectedModelId = ModelId;

        try
        {
            _processes = host.Processes;
        }
        catch (NotSupportedException)
        {
            _processes = null;
        }

        host.Log(
            _platformSupported && _processes is not null ? PluginLogLevel.Info : PluginLogLevel.Warning,
            _platformSupported && _processes is not null
                ? "Activated. Needle runtime and Whistle model download on demand."
                : "Needle Whistle requires Linux x86-64 and host-managed process supervision."
        );
        return Task.CompletedTask;
    }

    public async Task DeactivateAsync()
    {
        _processes = null;
        await _assetGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _inferenceGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _host = null;
            }
            finally
            {
                _inferenceGate.Release();
            }
        }
        finally
        {
            _assetGate.Release();
        }
    }

    public void SelectModel(string modelId)
    {
        if (!string.Equals(modelId, ModelId, StringComparison.Ordinal))
            throw new ArgumentException($"Unknown Needle Whistle model: {modelId}", nameof(modelId));
        _selectedModelId = ModelId;
        _host?.SetSetting("selectedModel", ModelId);
    }

    public bool IsModelDownloaded(string modelId)
    {
        EnsureModelId(modelId);
        if (_host is null)
            return false;
        return s_assets.All(asset => VerifyAsset(AssetPath(asset), asset));
    }

    public async Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        EnsureModelId(modelId);
        if (!IsConfigured)
            throw new PlatformNotSupportedException("Needle Whistle requires Linux x86-64 and host-managed process supervision.");

        var root = AssetRoot;
        Directory.CreateDirectory(root);
        var lockPath = Path.Join(root, ".download.lock");
        await _assetGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var lease = await InterProcessFileLock.AcquireAsync(lockPath, ct).ConfigureAwait(false);
            var neededBytes = s_assets
                .Where(asset => !VerifyAsset(AssetPath(asset), asset))
                .Sum(asset => asset.Size);
            DownloadSpace.EnsureAvailable(root, neededBytes, "Needle Whistle runtime and model");

            long completedBytes = s_assets
                .Where(asset => VerifyAsset(AssetPath(asset), asset))
                .Sum(asset => asset.Size);
            foreach (var asset in s_assets)
            {
                var path = AssetPath(asset);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (VerifyAsset(path, asset))
                {
                    if (asset.IsExecutable)
                        SetExecutable(path);
                    continue;
                }

                await ResilientDownloader.DownloadToFileAsync(
                    _httpClient,
                    asset.Url,
                    path,
                    approxTotalBytes: asset.Size,
                    idleTimeout: TimeSpan.FromSeconds(60),
                    allowResume: true,
                    onBytesOnDisk: onDisk => progress?.Report(
                        Math.Min(1, (double)(completedBytes + onDisk) / s_assets.Sum(item => item.Size))
                    ),
                    verifyComplete: downloadedPath => VerifyPinnedAsset(downloadedPath, asset),
                    ct,
                    new DownloadSpaceRequirement(
                        "Needle Whistle runtime and model",
                        s_assets.Where(other => other != asset && !VerifyAsset(AssetPath(other), other))
                            .Sum(other => other.Size)
                    )
                ).ConfigureAwait(false);

                if (asset.IsExecutable)
                    SetExecutable(path);
                completedBytes += asset.Size;
            }

            progress?.Report(1);
        }
        finally
        {
            _assetGate.Release();
        }
    }

    public Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        EnsureModelId(modelId);
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured)
            throw new PlatformNotSupportedException("Needle Whistle requires Linux x86-64 and host-managed process supervision.");
        if (!IsModelDownloaded(modelId))
            throw new FileNotFoundException("Download the Needle Whistle runtime and model before loading it.");

        _selectedModelId = ModelId;
        _host?.SetSetting("selectedModel", ModelId);
        _host?.Log(PluginLogLevel.Info, "Needle Whistle assets are verified and ready.");
        return Task.CompletedTask;
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct
    )
    {
        if (translate)
            throw new NotSupportedException("Whistle does not translate audio.");
        if (!IsConfigured)
            throw new PlatformNotSupportedException("Needle Whistle requires Linux x86-64 and host-managed process supervision.");
        var duration = NeedleWhistleContract.ReadWavDurationSeconds(wavAudio);
        var resolvedLanguage = NormalizeLanguage(language);
        var dictionaryTerms = PluginTranscriptionPrompt.Parse(prompt).DictionaryTerms;
        string? wavPath = null;
        string? keywordsPath = null;

        await _inferenceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsConfigured)
                throw new PlatformNotSupportedException("Needle Whistle requires Linux x86-64 and host-managed process supervision.");
            if (_selectedModelId != ModelId)
                throw new InvalidOperationException("Select the Whistle model before transcribing.");

            var host = _host ?? throw new InvalidOperationException("Needle Whistle is not activated.");
            var processes = _processes ?? throw new NotSupportedException("The plugin host does not provide process supervision.");
            var runnerPath = AssetPath(s_assets[0]);
            var modelPath = AssetPath(s_assets[1]);
            if (!IsModelDownloaded(ModelId))
                throw new FileNotFoundException("Download or repair the Needle Whistle runtime and model before transcribing.");

            var tempDirectory = Path.Join(host.PluginDataDirectory, "TemporaryAudio");
            Directory.CreateDirectory(tempDirectory);
            wavPath = Path.Join(tempDirectory, $"{Guid.NewGuid():N}.wav");
            await File.WriteAllBytesAsync(wavPath, wavAudio, ct).ConfigureAwait(false);
            if (dictionaryTerms.Count > 0)
            {
                keywordsPath = Path.Join(tempDirectory, $"{Guid.NewGuid():N}.txt");
                await File.WriteAllTextAsync(
                    keywordsPath,
                    string.Join('\n', dictionaryTerms),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    ct
                ).ConfigureAwait(false);
            }

            var args = NeedleWhistleContract.BuildArguments(modelPath, wavPath, resolvedLanguage, keywordsPath);
            var outcome = await processes.RunOneShotAsync(
                new ProcessCommand(
                    runnerPath,
                    args,
                    Environment: new Dictionary<string, string>
                    {
                        ["NEEDLE_TELEMETRY"] = "0",
                        ["DO_NOT_TRACK"] = "1",
                    },
                    WorkingDirectory: Path.GetDirectoryName(runnerPath)
                ),
                new ProcessOneShotOptions(
                    Timeout: s_processTimeout,
                    StandardOutput: ProcessCaptureMode.Utf8Text,
                    StandardError: ProcessCaptureMode.Utf8Text
                )
                {
                    MaximumStandardOutputBytes = (int)MaximumOutputBytes,
                    MaximumStandardErrorBytes = 64 * 1024,
                    PostExitPipePolicy = ProcessPostExitPipePolicy.AbandonAfterGrace,
                    PostExitDrainGrace = TimeSpan.FromMilliseconds(250),
                    ClearInheritedEnvironment = true,
                },
                ct
            ).ConfigureAwait(false);

            if (!outcome.Succeeded)
            {
                var detail = string.IsNullOrWhiteSpace(outcome.StandardErrorText)
                    ? $"exit status {outcome.ExitCode?.ToString() ?? outcome.Status.ToString()}"
                    : outcome.StandardErrorText.Trim();
                throw new InvalidOperationException($"Needle Whistle transcription failed: {detail}");
            }

            return NeedleWhistleContract.ParseResult(outcome.StandardOutputText, duration);
        }
        finally
        {
            if (wavPath is not null)
                TryDeleteFile(wavPath);
            if (keywordsPath is not null)
                TryDeleteFile(keywordsPath);
            _inferenceGate.Release();
        }
    }

    public async Task UnloadModelAsync()
    {
        await _inferenceGate.WaitAsync().ConfigureAwait(false);
        _inferenceGate.Release();
    }

    public async Task DeleteModelAsync(string modelId, CancellationToken ct)
    {
        EnsureModelId(modelId);
        await _assetGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _inferenceGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var asset in s_assets)
                    TryDeleteFile(AssetPath(asset));
                _selectedModelId = null;
            }
            finally
            {
                _inferenceGate.Release();
            }
        }
        finally
        {
            _assetGate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _httpClient.Dispose();
        _assetGate.Dispose();
        _inferenceGate.Dispose();
    }

    private string AssetRoot => Path.Join(
        _host?.PluginAssetDirectory ?? throw new InvalidOperationException("Needle Whistle is not activated."),
        "NeedleWhistle"
    );

    private string AssetPath(AssetDefinition asset) => Path.Join(AssetRoot, asset.RelativePath);

    private bool VerifyAsset(string path, AssetDefinition asset)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            using var stream = File.OpenRead(path);
            return stream.Length == asset.Size
                && string.Equals(
                    Convert.ToHexStringLower(SHA256.HashData(stream)),
                    asset.Sha256,
                    StringComparison.Ordinal
                );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void VerifyPinnedAsset(string path, AssetDefinition asset)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length != asset.Size)
            throw new InvalidDataException($"Downloaded {Path.GetFileName(path)} has the wrong size.");
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), asset.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException($"Downloaded {Path.GetFileName(path)} failed SHA-256 verification.");
    }

    private static void SetExecutable(string path)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The Needle runner is only available for Linux.");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
        );
    }

    private static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return null;
        var normalized = language.Trim().Split('-', '_')[0].ToLowerInvariant();
        if (!s_supportedLanguages.Contains(normalized, StringComparer.Ordinal))
            throw new NotSupportedException($"Whistle does not support the selected language '{language}'.");
        return normalized;
    }

    private static void EnsureModelId(string modelId)
    {
        if (!string.Equals(modelId, ModelId, StringComparison.Ordinal))
            throw new ArgumentException($"Unknown Needle Whistle model: {modelId}", nameof(modelId));
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private IPluginLocalization Loc => _host?.Localization ?? _injectedLocalization ?? NullLocalization.Instance;

    private sealed record AssetDefinition(string RelativePath, string Url, long Size, string Sha256, bool IsExecutable);

    private sealed class NullLocalization : IPluginLocalization
    {
        public static NullLocalization Instance { get; } = new();
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key switch
        {
            "Manifest.Name" => "Needle Whistle",
            "Manifest.Description" => "Local seven-language speech recognition with Cactus Whistle.",
            _ => key,
        };
        public string GetString(string key, params object[] args) => GetString(key);
    }
}
