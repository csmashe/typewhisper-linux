using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Runtime.InteropServices;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Translation;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services;

public sealed class TranslationService : ITranslationService, IDisposable
{
    private const string TranslationSystemPrompt =
        "You are a professional translator. Translate the given text accurately and naturally. "
        + "Output ONLY the translation, nothing else. Do not add explanations, notes, or formatting.";

    private static int s_onnxResolverRegistered;
    private readonly SemaphoreSlim _downloadSemaphore = new(1, 1);
    private readonly HttpClient _httpClient = new();
    private readonly Dictionary<string, LoadedTranslationModel> _loadedModels = new();

    private readonly PluginManager _pluginManager;
    private bool _disposed;

    public TranslationService(PluginManager pluginManager)
    {
        RegisterOnnxRuntimeResolver();
        _pluginManager = pluginManager;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _httpClient.Dispose();
        _downloadSemaphore.Dispose();

        foreach (var model in _loadedModels.Values)
        {
            model.Encoder.Dispose();
            model.Decoder.Dispose();
        }

        _loadedModels.Clear();
        _disposed = true;
    }

    public async Task<string> TranslateAsync(
        string text,
        string sourceLang,
        string targetLang,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(text) || sourceLang == targetLang)
        {
            return text;
        }

        var llmProvider = GetConfiguredTranslationProvider();
        if (llmProvider is null)
        {
            return await TranslateLocalAsync(text, sourceLang, targetLang, ct);
        }

        var model = llmProvider.SupportedModels[0].Id;
        var userText = $"Translate from {sourceLang} to {targetLang}:\n\n{text}";
        return await llmProvider.ProcessAsync(TranslationSystemPrompt, userText, model, ct);
    }

    private ILlmProviderPlugin? GetConfiguredTranslationProvider()
    {
        return _pluginManager.LlmProviders.FirstOrDefault(provider => provider.IsAvailable);
    }

    private async Task<string> TranslateLocalAsync(
        string text,
        string sourceLang,
        string targetLang,
        CancellationToken ct
    )
    {
        var directModel = TranslationModelInfo.FindModel(sourceLang, targetLang);
        if (directModel is not null)
        {
            var model = await GetOrLoadModelAsync(sourceLang, targetLang, ct);
            return await Task.Run(() => RunInference(model, text), ct);
        }

        // No direct model — try a two-hop English pivot (e.g. FR→EN→DE)
        // so a single model set covers many indirect language pairs.
        if (sourceLang != "en" && targetLang != "en")
        {
            var toEnglish = TranslationModelInfo.FindModel(sourceLang, "en");
            var fromEnglish = TranslationModelInfo.FindModel("en", targetLang);

            if (toEnglish is not null && fromEnglish is not null)
            {
                var first = await GetOrLoadModelAsync(sourceLang, "en", ct);
                var english = await Task.Run(() => RunInference(first, text), ct);

                var second = await GetOrLoadModelAsync("en", targetLang, ct);
                return await Task.Run(() => RunInference(second, english), ct);
            }
        }

        throw new NotSupportedException(
            $"Translation is not available for {sourceLang} -> {targetLang}."
        );
    }

    private async Task<LoadedTranslationModel> GetOrLoadModelAsync(
        string sourceLang,
        string targetLang,
        CancellationToken ct
    )
    {
        var key = ModelKey(sourceLang, targetLang);
        if (_loadedModels.TryGetValue(key, out var model))
        {
            return model;
        }

        return await EnsureModelLoadedAsync(sourceLang, targetLang, ct);
    }

    private async Task<LoadedTranslationModel> EnsureModelLoadedAsync(
        string sourceLang,
        string targetLang,
        CancellationToken ct
    )
    {
        var key = ModelKey(sourceLang, targetLang);

        await _downloadSemaphore.WaitAsync(ct);
        try
        {
            if (_loadedModels.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var modelInfo =
                TranslationModelInfo.FindModel(sourceLang, targetLang)
                ?? throw new NotSupportedException(
                    $"No translation model for {sourceLang} -> {targetLang}."
                );

            var modelDir = Path.Join(TypeWhisperEnvironment.ModelsPath, modelInfo.SubDirectory);
            Directory.CreateDirectory(modelDir);

            await DownloadMissingFilesAsync(modelInfo, modelDir, ct);

            var loaded = LoadModel(modelDir);
            _loadedModels[key] = loaded;

            return loaded;
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    private async Task DownloadMissingFilesAsync(
        TranslationModelInfo modelInfo,
        string modelDir,
        CancellationToken ct
    )
    {
        foreach (var file in modelInfo.Files)
        {
            var filePath = Path.Join(modelDir, file.FileName);
            if (File.Exists(filePath))
            {
                continue;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUrl);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct
            );
            response.EnsureSuccessStatusCode();

            var tempPath = filePath + ".tmp";
            await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
            await using (
                var fileStream = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    true
                )
            )
            {
                await contentStream.CopyToAsync(fileStream, ct);
            }

            File.Move(tempPath, filePath, true);
        }
    }

    private static LoadedTranslationModel LoadModel(string modelDir)
    {
        RegisterOnnxRuntimeResolver();
        var config = MarianConfig.Load(Path.Join(modelDir, "config.json"));
        var tokenizer = MarianTokenizer.Load(
            Path.Join(modelDir, "tokenizer.json"),
            config.EosTokenId
        );

        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Environment.ProcessorCount
        };

        var encoder = new InferenceSession(
            Path.Join(modelDir, "encoder_model_quantized.onnx"),
            sessionOptions
        );
        var decoder = new InferenceSession(
            Path.Join(modelDir, "decoder_model_quantized.onnx"),
            sessionOptions
        );

        return new LoadedTranslationModel(encoder, decoder, tokenizer, config);
    }

    /// <summary>
    ///     Greedy decoder over the encoder/decoder pair. MarianMT ONNX exports
    ///     have no KV cache, so each step re-attends the full history (O(n²)),
    ///     which is acceptable for short speech sentences.
    /// </summary>
    private static string RunInference(LoadedTranslationModel model, string text)
    {
        var inputIds = model.Tokenizer.Encode(text);
        var seqLen = inputIds.Length;

        var inputIdsTensor = new DenseTensor<long>(
            inputIds.Select(id => (long)id).ToArray(),
            [1, seqLen]
        );
        var attentionMask = new DenseTensor<long>(
            Enumerable.Repeat(1L, seqLen).ToArray(),
            [1, seqLen]
        );

        using var encoderResults = model.Encoder.Run([
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask)
        ]);

        var encoderHidden =
            encoderResults[0].Value as DenseTensor<float>
            ?? throw new InvalidOperationException("Encoder output is not a float tensor.");

        var maxTokens = Math.Min(model.Config.MaxLength, 200);
        var decodedIds = new List<int> { model.Config.DecoderStartTokenId };

        for (var step = 0; step < maxTokens; step++)
        {
            var decoderLength = decodedIds.Count;
            var decoderInputIds = new DenseTensor<long>(
                decodedIds.Select(id => (long)id).ToArray(),
                [1, decoderLength]
            );

            var decoderInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", decoderInputIds),
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", attentionMask),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderHidden)
            };

            using var decoderResults = model.Decoder.Run(decoderInputs);
            var logits =
                decoderResults[0].Value as DenseTensor<float>
                ?? throw new InvalidOperationException("Decoder output is not a float tensor.");

            var vocabSize = logits.Dimensions[2];
            var lastTokenOffset = (decoderLength - 1) * vocabSize;
            var bestId = 0;
            var bestValue = float.NegativeInfinity;

            for (var i = 0; i < vocabSize; i++)
            {
                var candidate = logits.Buffer.Span[lastTokenOffset + i];
                if (candidate <= bestValue)
                {
                    continue;
                }

                bestValue = candidate;
                bestId = i;
            }

            if (bestId == model.Config.EosTokenId)
            {
                break;
            }

            decodedIds.Add(bestId);
        }

        return model.Tokenizer.Decode(decodedIds.ToArray().AsSpan(1));
    }

    private static string ModelKey(string sourceLang, string targetLang)
    {
        return $"{sourceLang}-{targetLang}";
    }

    private static void RegisterOnnxRuntimeResolver()
    {
        if (Interlocked.Exchange(ref s_onnxResolverRegistered, 1) == 1)
        {
            return;
        }

        // The managed loader doesn't find libonnxruntime.so automatically on
        // Linux when published single-file/self-contained, so resolve manually.
        NativeLibrary.SetDllImportResolver(
            typeof(InferenceSession).Assembly,
            (libraryName, _, _) =>
            {
                if (!libraryName.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase))
                {
                    return IntPtr.Zero;
                }

                var rid = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.Arm64 => "linux-arm64",
                    _ => "linux-x64"
                };

                var candidate = Path.Join(
                    AppContext.BaseDirectory,
                    "runtimes",
                    rid,
                    "native",
                    "libonnxruntime.so"
                );
                return File.Exists(candidate) ? NativeLibrary.Load(candidate) : IntPtr.Zero;
            }
        );
    }
}

internal sealed record LoadedTranslationModel(
    InferenceSession Encoder,
    InferenceSession Decoder,
    MarianTokenizer Tokenizer,
    MarianConfig Config
);