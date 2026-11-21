namespace TypeWhisper.Linux.Services;

/// <summary>Records whether a dictation's Medium or High cleanup fell back to Light.</summary>
internal sealed class CleanupFallback
{
    private volatile bool _occurred;

    public bool Occurred => _occurred;

    public void Mark() => _occurred = true;
}
