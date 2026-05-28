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
    private string _confirmedText = "";
    private string _lastDisplayedText = "";
    private int _sessionVersion;

    public int StartSession()
    {
        // Bump the version before clearing so any in-flight writer with the
        // old version fails its re-check and can't clobber the fresh buffers.
        var newVersion = Interlocked.Increment(ref _sessionVersion);
        _confirmedText = "";
        _lastDisplayedText = "";
        return newVersion;
    }

    public string StopSession()
    {
        // If the last displayed text diverged from confirmed (e.g. a partial
        // that the corrector hasn't committed yet), discard both to avoid
        // surfacing a half-baked result as the final transcript.
        var hasUncommittedPreview = _lastDisplayedText != _confirmedText;
        var finalText = hasUncommittedPreview ? "" : _confirmedText;
        InvalidateSession();
        _confirmedText = "";
        _lastDisplayedText = "";
        return finalText;
    }

    public bool IsCurrentSession(int sessionVersion)
    {
        return sessionVersion == Volatile.Read(ref _sessionVersion);
    }

    public void InvalidateSession()
    {
        Interlocked.Increment(ref _sessionVersion);
    }

    public bool TryApplyPolling(
        int sessionVersion,
        string rawText,
        Func<string, string> corrector,
        out string displayText
    )
    {
        displayText = "";
        if (!IsCurrentSession(sessionVersion))
        {
            return false;
        }

        var text = rawText.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        text = corrector(text);
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var stable = StabilizeText(_confirmedText, text);

        // Re-check immediately before writing: a StartSession/InvalidateSession
        // may have bumped the version while we were corrector-ing and stabilizing.
        if (!IsCurrentSession(sessionVersion))
        {
            return false;
        }

        _confirmedText = stable;
        _lastDisplayedText = stable;
        displayText = stable;
        return true;
    }

    /// <summary>
    ///     Merges a new partial transcript into the confirmed accumulator,
    ///     preventing regressions where the model re-emits an earlier shorter
    ///     hypothesis that would erase already-confirmed text.
    ///     Strategy (in order of preference):
    ///     1. If newText extends confirmed (starts with it), accept the extension.
    ///     2. If the common prefix covers more than half of confirmed, splice
    ///     the diverging tail onto the end of confirmed.
    ///     3. Walk a sliding window backwards through confirmed looking for a
    ///     suffix that newText starts with (model has backed up slightly
    ///     but the new hypothesis still overlaps). Append the non-overlapping
    ///     tail to confirmed.
    ///     4. Fallback: trust newText entirely (major hypothesis revision).
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
        if (maxShift > 0)
        {
            for (var dropCount = 1; dropCount <= maxShift; dropCount++)
            {
                var suffix = confirmed[dropCount..];
                if (newText.StartsWith(suffix, StringComparison.Ordinal))
                {
                    var newTail = newText[(confirmed.Length - dropCount)..];
                    return string.IsNullOrEmpty(newTail) ? confirmed : confirmed + newTail;
                }
            }
        }

        return newText;
    }
}