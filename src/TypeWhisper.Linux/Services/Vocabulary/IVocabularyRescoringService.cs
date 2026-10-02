using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services.Vocabulary;

public sealed record VocabularyRescoringInput(
    Guid RecordingId,
    string Text,
    byte[] Wav,
    IReadOnlyList<VocabularyTokenTiming> TokenTimings,
    bool TranslateRequested,
    string? EngineProviderId,
    string? EngineModelId
);

public sealed record VocabularyRescoringOutcome(
    string Text,
    bool Eligible,
    bool Applied,
    string? Error
);

public interface IVocabularyRescoringService
{
    // The loaded model for the Dictionary panel's status; a transcript is judged by the engine
    // that produced it, captured under its transcription lease.
    string? ActiveEngineBlocker { get; }
    bool IsEligible(
        IReadOnlyList<VocabularyTokenTiming> tokenTimings,
        bool translateRequested,
        string? engineProviderId,
        string? engineModelId
    );
    Task<VocabularyRescoringOutcome> RefineAsync(
        VocabularyRescoringInput input,
        CancellationToken ct
    );
}
