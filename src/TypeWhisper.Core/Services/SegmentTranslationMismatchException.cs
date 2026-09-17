namespace TypeWhisper.Core.Services;

public sealed class SegmentTranslationMismatchException : Exception
{
    public SegmentTranslationMismatchException()
        : base("Translation did not preserve the subtitle segments. Retry with another LLM model.")
    {
    }
}
