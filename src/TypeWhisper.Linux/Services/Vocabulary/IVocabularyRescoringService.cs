using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services.Vocabulary;

public sealed record VocabularyRescoringInput(
    Guid RecordingId,
    string Text,
    byte[] Wav,
    IReadOnlyList<VocabularyTokenTiming> TokenTimings,
    bool TranslateRequested
);

public sealed record VocabularyRescoringOutcome(
    string Text,
    bool Eligible,
    bool Applied,
    string? Error
);

public interface IVocabularyRescoringService
{
    bool IsEligible(IReadOnlyList<VocabularyTokenTiming> tokenTimings, bool translateRequested);
    Task<VocabularyRescoringOutcome> RefineAsync(
        VocabularyRescoringInput input,
        CancellationToken ct
    );
}
