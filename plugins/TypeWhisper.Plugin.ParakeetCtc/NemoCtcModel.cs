using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace TypeWhisper.Plugin.ParakeetCtc;

public sealed record CtcEmission(
    float[] LogProbabilities,
    int Frames,
    int VocabularySize,
    double FrameSeconds
);

public sealed class NemoCtcModel : IDisposable
{
    internal const int Subsampling = 8;
    private readonly InferenceSession? _session;
    private readonly Action? _onDispose;
    public IReadOnlyDictionary<string, string> Metadata { get; }

    internal NemoCtcModel(IReadOnlyDictionary<string, string> metadata, Action onDispose)
    {
        Metadata = metadata;
        _onDispose = onDispose;
    }

    public NemoCtcModel(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("CTC model file is missing.", path);
        OnnxRuntimeNativeLibrary.EnsureRegistered();
        using var options = new SessionOptions();
        options.IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        options.InterOpNumThreads = 1;
        _session = new InferenceSession(path, options);
        Metadata = _session.ModelMetadata.CustomMetadataMap;
    }

    public CtcEmission Evaluate(ReadOnlyMemory<float> audio, CancellationToken cancellation)
    {
        if (_session is null)
            throw new InvalidOperationException("The test model cannot evaluate audio.");
        if (audio.Length > 16000 * 30)
            throw new NotSupportedException("Score a bounded audio window of at most 30 seconds.");
        var (features, frames) = NemoFeatures.Extract(audio.Span, cancellation);
        var floatInput = _session
            .InputMetadata.Single(p => p.Value.ElementType == typeof(float))
            .Key;
        var lengthInput = _session
            .InputMetadata.Single(p => p.Value.ElementType == typeof(long))
            .Key;
        using var results = _session.Run([
            NamedOnnxValue.CreateFromTensor(
                floatInput,
                new DenseTensor<float>(features, [1, 80, frames])
            ),
            NamedOnnxValue.CreateFromTensor(
                lengthInput,
                new DenseTensor<long>(new long[] { frames }, [1])
            ),
        ]);
        cancellation.ThrowIfCancellationRequested();
        var output = results[0].AsTensor<float>();
        if (output.Dimensions.Length != 3 || output.Dimensions[0] != 1)
            throw new InvalidDataException("Unexpected CTC output shape.");
        var steps = output.Dimensions[1];
        var vocabulary = output.Dimensions[2];
        var probabilities = output.ToArray();
        // Stable log-softmax works for logits and already-normalized log probabilities.
        for (var t = 0; t < steps; t++)
        {
            var offset = t * vocabulary;
            var max = float.NegativeInfinity;
            for (var v = 0; v < vocabulary; v++)
                max = Math.Max(max, probabilities[offset + v]);
            double sum = 0;
            for (var v = 0; v < vocabulary; v++)
                sum += Math.Exp(probabilities[offset + v] - max);
            var logSum = Math.Log(sum);
            for (var v = 0; v < vocabulary; v++)
                probabilities[offset + v] = (float)(probabilities[offset + v] - max - logSum);
        }
        // Emissions stay one subsampled feature hop apart; the last step also covers
        // the partial stride, so dividing the duration by the step count would
        // compress the time scale of every window that is not a multiple of it.
        return new CtcEmission(
            probabilities,
            steps,
            vocabulary,
            NemoFeatures.Hop * Subsampling / 16000d
        );
    }

    public void Dispose()
    {
        _session?.Dispose();
        _onDispose?.Invoke();
    }
}
