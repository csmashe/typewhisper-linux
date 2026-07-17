namespace TypeWhisper.Linux.Services;

/// <summary>
///     Accumulates and stabilizes partial transcription results during a live
///     dictation session. Each call to <see cref="StartSession" /> bumps a
///     version counter so in-flight writers from the previous session can
///     detect the staleness and discard their results without corrupting the
///     next session's text.
/// </summary>
internal sealed class StreamingTranscriptState
{
    // Keep corrector work outside this lock: correctors can take their own locks and do
    // non-trivial list/regex work, so a full-method lock would make StartSession and
    // StopSession wait behind them and introduce nested-lock ordering. Instead, snapshot
    // under this lock and compare-and-commit under it after correction/stabilization.
    private readonly Lock _lock = new();
    private string _confirmedText = "";
    private string _lastDisplayedText = "";
    private int _sessionVersion;

    public int StartSession()
    {
        lock (_lock)
        {
            _sessionVersion++;
            _confirmedText = "";
            _lastDisplayedText = "";
            return _sessionVersion;
        }
    }

    public string StopSession()
    {
        lock (_lock)
        {
            var finalText = !string.IsNullOrWhiteSpace(_lastDisplayedText)
                ? _lastDisplayedText
                : _confirmedText;
            _sessionVersion++;
            _confirmedText = "";
            _lastDisplayedText = "";
            return finalText;
        }
    }

    public bool TryApplyPolling(
        int sessionVersion,
        string rawText,
        Func<string, string> corrector,
        out string displayText
    )
    {
        displayText = "";
        string confirmedSnapshot;
        lock (_lock)
        {
            if (sessionVersion != _sessionVersion)
            {
                return false;
            }

            confirmedSnapshot = _confirmedText;
        }

        var text = rawText.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        // Deliberately outside the lock; see the trade-off note on _lock.
        text = corrector(text);
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var stable = StabilizeText(confirmedSnapshot, text);

        lock (_lock)
        {
            // A version check alone cannot detect another poll committing within this same
            // session; the value compare discards this stale result instead of clobbering it.
            if (sessionVersion != _sessionVersion || _confirmedText != confirmedSnapshot)
            {
                return false;
            }

            _confirmedText = stable;
            _lastDisplayedText = stable;
            displayText = stable;
            return true;
        }
    }

    /// <summary>
    ///     Merges a new partial transcript into the confirmed accumulator, preventing
    ///     regressions where the model re-emits a shorter hypothesis erasing confirmed text.
    ///     Strategy: (1) if newText extends confirmed, accept it; (2) if common prefix
    ///     covers &gt;half of confirmed, splice the tail; (3) sliding-window backward search
    ///     for a confirmed suffix that newText starts with; (4) fallback: trust newText.
    /// </summary>
    internal static string StabilizeText(string confirmed, string newText)
    {
        newText = newText.Trim();
        if (string.IsNullOrEmpty(confirmed))
        {
            return newText;
        }

        if (string.IsNullOrEmpty(newText))
        {
            return confirmed;
        }

        if (newText.StartsWith(confirmed, StringComparison.Ordinal))
        {
            return newText;
        }

        var matchEnd = 0;
        var minLen = Math.Min(confirmed.Length, newText.Length);
        for (var i = 0; i < minLen; i++)
        {
            if (confirmed[i] == newText[i])
            {
                matchEnd = i + 1;
            }
            else
            {
                break;
            }
        }

        if (matchEnd > confirmed.Length / 2)
        {
            var tail = newText[matchEnd..];
            if (tail.Length > 0 && !confirmed.EndsWith(' ') && !tail.StartsWith(' '))
            {
                return confirmed + " " + tail;
            }

            return confirmed + tail;
        }

        var minOverlap = Math.Max(1, Math.Min(20, confirmed.Length / 4));
        var maxShift = Math.Min(confirmed.Length - minOverlap, 150);
        if (maxShift <= 0)
        {
            return newText;
        }

        for (var dropCount = 1; dropCount <= maxShift; dropCount++)
        {
            var suffix = confirmed[dropCount..];
            if (!newText.StartsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var newTail = newText[(confirmed.Length - dropCount)..];
            return string.IsNullOrEmpty(newTail) ? confirmed : confirmed + newTail;
        }

        return newText;
    }
}
