namespace TypeWhisper.Core.Models;

/// <summary>
///     Transient, per-dictation accumulator for <see cref="LlmCallProvenance" />
///     entries. Pipeline steps run sequentially, but the streaming→batch fallback
///     and background steps can touch it from different threads, so a plain list
///     guarded by a lock is sufficient. Not persisted directly — its contents are
///     copied onto the <see cref="TranscriptionRecord" /> when the entry is built.
/// </summary>
public sealed class LlmCallCapture
{
    private readonly List<LlmCallProvenance> _calls = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<LlmCallProvenance> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    public void Add(LlmCallProvenance call)
    {
        lock (_gate)
        {
            _calls.Add(call);
        }
    }
}
