namespace TypeWhisper.Linux.Services;

/// <summary>Records whether a cleanup request carried a dictation's captured reference context.</summary>
internal sealed class ReferenceContextUse
{
    private volatile bool _sent;

    public bool Sent => _sent;

    public void MarkSent() => _sent = true;
}
