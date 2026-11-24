using System.Buffers.Binary;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GigaAm;

/// <summary>
///     Local Russian speech recognition with GigaAM v3 by Sber. The end-to-end RNN-T model adds
///     punctuation and capitalization and runs on the CPU through ONNX Runtime; it only
///     recognizes Russian.
/// </summary>
public sealed class GigaAmPlugin
    : ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
        IPluginLocalizationAware
{
    internal const string ModelId = "gigaam-v3-e2e-rnnt";
    private const string PreprocessorFileName = "gigaam_v3_preprocessor.onnx";
    private const string DefaultModelName = "GigaAM v3 (Russian, with punctuation)";
    private static readonly IReadOnlyList<string> s_russian = ["ru"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http;
    private readonly GigaAmModelAssets _assets;
    private readonly Func<string, string, IGigaAmRecognizer> _factory;
    private readonly long _megabytes;
    private IPluginHostServices? _host;
    private IPluginLocalization? _injectedLocalization;
    private IGigaAmRecognizer? _recognizer;
    // Cancelled before deactivation waits for the gate, so a download in flight ends promptly.
    private CancellationTokenSource? _activation;
    private bool _disposed;

    public GigaAmPlugin()
        : this(
            // A 2 h ceiling for the 214 MB encoder; the downloader's idle watchdog catches a
            // half-open socket much sooner.
            new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
            {
                Timeout = TimeSpan.FromHours(2),
            },
            (modelDirectory, preprocessor) => new GigaAmRuntime(modelDirectory, preprocessor)
        ) { }

    // Test seam: the factory receives the model directory and the preprocessor path.
    internal GigaAmPlugin(
        HttpClient http,
        Func<string, string, IGigaAmRecognizer> factory,
        GigaAmModelAssets? assets = null
    )
    {
        _http = http;
        _factory = factory;
        _assets =
            assets
            ?? new GigaAmModelAssets(
                http,
                GigaAmModelAssets.DefaultBaseUrl,
                GigaAmModelAssets.DefaultFiles
            );
        _megabytes = (long)Math.Round(_assets.TotalBytes / (1024d * 1024));
    }

    public string PluginId => "com.typewhisper.gigaam";
    public string PluginName => "GigaAM";
    public string PluginVersion => PluginBuildInfo.Version;
    public string ProviderId => "gigaam";

    // English when no catalog is available (L would return the bare key).
    public string ProviderDisplayName => Loc is null ? "GigaAM" : Loc.L("Provider.DisplayName");

    // A local engine needs no key; whether the model is downloaded is reported per model.
    public bool IsConfigured => true;
    public string? SelectedModelId { get; private set; }
    public bool SupportsTranslation => false;
    public bool SupportsModelDownload => true;

    /// <summary>GigaAM v3 only recognizes Russian.</summary>
    public IReadOnlyList<string> SupportedLanguages => s_russian;

    // Automatic always yields Russian; an explicit choice other than Russian is refused by the
    // host through SupportedLanguages.
    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;

    public IReadOnlyList<PluginModelInfo> TranscriptionModels =>
    [
        new(ModelId, Loc is null ? DefaultModelName : Loc.L("Model.DisplayName"))
        {
            SizeDescription = $"~{_megabytes} MB",
            EstimatedSizeMB = _megabytes,
            IsRecommended = true,
            LanguageCount = 1,
            SupportsTranslation = false,
        },
    ];

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // The host's catalog once activated, else the one injected at load.
    private IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    /// <summary>The feature extractor ships with the plugin; only the model weights are downloaded.</summary>
    internal static string PreprocessorPath =>
        Path.Join(
            Path.GetDirectoryName(typeof(GigaAmPlugin).Assembly.Location)!,
            PreprocessorFileName
        );

    public Task ActivateAsync(IPluginHostServices host)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _activation ??= new CancellationTokenSource();
        _host = host;
        return Task.CompletedTask;
    }

    public async Task DeactivateAsync()
    {
        var activation = EndActivation();
        // ReSharper disable once MethodSupportsCancellation -- deactivation has no token and must wait for the cancelled work to leave.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Release();
            _host = null;
            activation?.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SelectModel(string modelId)
    {
        ValidateModel(modelId);
        SelectedModelId = modelId;
    }

    public bool IsModelDownloaded(string modelId) =>
        modelId == ModelId && _host is not null && _assets.IsReady(ModelDirectory);

    public async Task DownloadModelAsync(
        string modelId,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        ValidateModel(modelId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var download = CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                EnsureActive()
            );
            await _assets
                .DownloadAsync(ModelDirectory, progress, download.Token)
                .ConfigureAwait(false);
            _host!.NotifyCapabilitiesChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteModelAsync(string modelId, CancellationToken ct)
    {
        ValidateModel(modelId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureActive();
            ct.ThrowIfCancellationRequested();
            Release();
            if (Directory.Exists(ModelDirectory))
            {
                Directory.Delete(ModelDirectory, recursive: true);
            }

            if (SelectedModelId == modelId)
            {
                SelectedModelId = null;
            }

            _host!.NotifyCapabilitiesChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        ValidateModel(modelId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnloadModelAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Release();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     Transcribes 16 kHz mono PCM16 WAV as Russian, whatever <paramref name="language" /> says:
    ///     the host only offers automatic or Russian. Audio over 20 s is cut at quiet points.
    /// </summary>
    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct
    )
    {
        if (translate)
        {
            throw new NotSupportedException("GigaAM cannot translate. It only transcribes Russian.");
        }

        ct.ThrowIfCancellationRequested();
        var samples = DecodeWav(wavAudio);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureActive();
            if (samples.Length == 0)
            {
                return new PluginTranscriptionResult("", "ru", 0);
            }

            await EnsureLoadedAsync(ct).ConfigureAwait(false);
            var recognizer = _recognizer!;
            return await Task.Run(() => Transcribe(recognizer, samples, ct), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static PluginTranscriptionResult Transcribe(
        IGigaAmRecognizer recognizer,
        float[] samples,
        CancellationToken ct
    )
    {
        const double sampleRate = GigaAmRuntime.SampleRate;
        var transcript = "";
        var segments = new List<PluginTranscriptionSegment>();
        foreach (
            var (offset, count) in GigaAmChunking.ChunkRanges(
                samples,
                GigaAmChunking.MaximumChunkSeconds * GigaAmRuntime.SampleRate
            )
        )
        {
            ct.ThrowIfCancellationRequested();
            var text = GigaAmChunking.Continuing(
                transcript,
                recognizer.Transcribe(samples[offset..(offset + count)], ct).Trim()
            );
            ct.ThrowIfCancellationRequested();
            if (text.Length == 0)
            {
                continue;
            }

            transcript = GigaAmChunking.Append(transcript, text);
            segments.Add(
                new PluginTranscriptionSegment(text, offset / sampleRate, (offset + count) / sampleRate)
            );
        }

        return new PluginTranscriptionResult(transcript, "ru", samples.Length / sampleRate)
        {
            Segments = segments,
        };
    }

    private string ModelDirectory => Path.Join(_host!.PluginAssetDirectory, "Models", ModelId);

    /// <returns>The token that deactivation cancels.</returns>
    private CancellationToken EnsureActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_host is null || Volatile.Read(ref _activation) is not { } activation)
        {
            throw new InvalidOperationException("The GigaAM plugin is not active.");
        }

        return activation.Token;
    }

    private CancellationTokenSource? EndActivation()
    {
        var activation = Interlocked.Exchange(ref _activation, null);
        activation?.Cancel();
        return activation;
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        EnsureActive();
        ct.ThrowIfCancellationRequested();
        if (_recognizer is not null)
        {
            return;
        }

        var directory = ModelDirectory;
        if (!_assets.IsReady(directory))
        {
            throw new InvalidOperationException("Download the GigaAM model before using it.");
        }

        var loaded = await Task.Run(() => _factory(directory, PreprocessorPath), ct)
            .ConfigureAwait(false);
        if (ct.IsCancellationRequested)
        {
            loaded.Dispose();
            ct.ThrowIfCancellationRequested();
        }

        _recognizer = loaded;
        _host!.Log(PluginLogLevel.Info, $"Loaded model {ModelId}");
    }

    private void Release()
    {
        _recognizer?.Dispose();
        _recognizer = null;
    }

    private static void ValidateModel(string modelId)
    {
        if (modelId != ModelId)
        {
            throw new ArgumentException($"Unknown model: {modelId}", nameof(modelId));
        }
    }

    /// <summary>Reads 16-bit mono 16 kHz PCM from a RIFF/WAVE file.</summary>
    internal static float[] DecodeWav(byte[] wav)
    {
        var data = wav.AsSpan();
        if (
            data.Length < 12
            || !data[..4].SequenceEqual("RIFF"u8)
            || !data.Slice(8, 4).SequenceEqual("WAVE"u8)
        )
        {
            throw new ArgumentException("Expected a PCM16 mono 16 kHz WAV file.", nameof(wav));
        }

        var formatFound = false;
        var position = 12;
        while (position <= data.Length - 8)
        {
            var id = data.Slice(position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position + 4, 4));
            // A streaming writer (ffmpeg on a pipe) leaves the size at 0xFFFFFFFF, and a recorder
            // stopped abruptly can leave it larger than the file; read what is there.
            var available = (int)Math.Min(size, (uint)(data.Length - position - 8));
            var chunk = data.Slice(position + 8, available);
            if (id.SequenceEqual("fmt "u8))
            {
                if (
                    chunk.Length < 16
                    || BinaryPrimitives.ReadUInt16LittleEndian(chunk) != 1
                    || BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]) != 1
                    || BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != GigaAmRuntime.SampleRate
                    || BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]) != 16
                )
                {
                    throw new ArgumentException(
                        "Expected PCM16 mono audio sampled at 16 kHz.",
                        nameof(wav)
                    );
                }

                formatFound = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (!formatFound)
                {
                    break;
                }

                var samples = new float[chunk.Length / 2];
                for (var index = 0; index < samples.Length; index++)
                {
                    samples[index] =
                        BinaryPrimitives.ReadInt16LittleEndian(chunk[(2 * index)..]) / 32768f;
                }

                return samples;
            }

            position += 8 + available + (int)(size % 2);
        }

        throw new ArgumentException("Missing or invalid WAV chunks.", nameof(wav));
    }

    public void Dispose()
    {
        var activation = EndActivation();
        // ReSharper disable once MethodSupportsCancellation -- Dispose has no token and must wait for the cancelled work to leave.
        _gate.Wait();
        try
        {
            activation?.Dispose();
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Release();
            _host = null;
            _http.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }
}
