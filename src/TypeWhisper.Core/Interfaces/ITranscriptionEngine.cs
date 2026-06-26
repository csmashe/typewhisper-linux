using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
///     A speech-to-text engine that loads a model and transcribes raw audio samples.
///     Implementations wrap a specific backend (whisper.cpp, sherpa-onnx, and so on).
/// </summary>
public interface ITranscriptionEngine
{
    /// <summary>True once a model has been loaded and is ready to transcribe.</summary>
    bool IsModelLoaded { get; }

    // ReSharper disable once UnusedMember.Global
    // ReSharper disable UnusedParameter.Global
    Task LoadModelAsync(string modelPath, CancellationToken cancellationToken = default);
    // ReSharper restore UnusedParameter.Global
    // ReSharper disable once UnusedMember.Global
    void UnloadModel();

    /// <summary>
    ///     Transcribes mono <paramref name="audioSamples" />. <paramref name="language" /> is an
    ///     optional hint (auto-detected when <c>null</c>); <paramref name="task" /> selects plain
    ///     transcription or speech translation to English.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples,
        string? language = null,
        TranscriptionTask task = TranscriptionTask.Transcribe,
        CancellationToken cancellationToken = default
    );
}
