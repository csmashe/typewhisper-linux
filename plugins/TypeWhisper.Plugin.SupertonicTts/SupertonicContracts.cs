namespace TypeWhisper.Plugin.SupertonicTts;

internal interface ISupertonicAssetManager
{
    string AssetRoot { get; }
    long TotalSizeBytes { get; }
    bool AreAssetsReady { get; }
    bool HasAnyAssets { get; }
    Task<bool> VerifyCachedAssetsAsync(CancellationToken ct);
    Task DownloadMissingAssetsAsync(IProgress<double>? progress, CancellationToken ct);
    Task RemoveAssetsAsync(CancellationToken ct);
}

internal interface ISupertonicSynthesizer : IDisposable
{
    SupertonicSynthesisResult Synthesize(SupertonicSynthesisRequest request, CancellationToken ct);
}

internal sealed record SupertonicSynthesisRequest(
    string Text,
    string Language,
    string VoiceStylePath,
    int DenoisingSteps,
    double Speed);

internal sealed record SupertonicSynthesisResult(float[] Samples, int SampleRate);
