using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services;

internal sealed class LiveCleanupDraft : IDisposable
{
    private readonly Lock _gate = new();
    private readonly LiveCleanupSession _session;
    private readonly LlmCleanupService _cleanup;
    private string? _capturedInput;
    private LlmCallCapture? _capture;
    private int _requests, _reused;
    public int RequestCount => Volatile.Read(ref _requests);
    public bool Reused => Volatile.Read(ref _reused) != 0;
    public CleanupLevel Level { get; }
    public string? Language { get; }
    public string Provider { get; }

    public LiveCleanupDraft(LlmCleanupService cleanup, CleanupLevel level, string? language,
        string provider, int timeoutSeconds, Action<string, string> publish)
    {
        Level = level;
        _cleanup = cleanup;
        Language = language;
        Provider = provider;
        _session = new LiveCleanupSession(async (input, token) =>
        {
            Interlocked.Increment(ref _requests);
            var capture = new LlmCallCapture();
            var output = await cleanup.CleanAsync(input, level, capture: capture,
                timeout: TimeSpan.FromSeconds(Math.Max(0, timeoutSeconds)), ct: token,
                sourceLanguage: language, providerOverride: provider, preparedLightText: true).ConfigureAwait(false);
            if (!capture.Calls.Any(call => !string.IsNullOrWhiteSpace(call.ResponseReceived))) return null;
            lock (_gate) { _capturedInput = input; _capture = capture; }
            return output;
        }, publish);
    }

    public void Submit(string input) => _session.Submit(_cleanup.PrepareLiveInput(input, Language));
    public void StopScheduling() => _session.StopScheduling();
    public bool Matches(CleanupLevel level, string? language, string? provider) =>
        Level == level && string.Equals(Language, language, StringComparison.OrdinalIgnoreCase) && Provider == provider;
    public bool MatchesFinal(CleanupLevel level, string? provider) => Level == level && Provider == provider;

    public async Task<string?> ReuseAsync(string input, string? finalLanguage, LlmCallCapture? capture, CancellationToken token)
    {
        // Language affects only the local pass. Reuse requires the exact text that
        // would reach the LLM after that pass, including any newly detected language.
        input = _cleanup.PrepareLiveInput(input, finalLanguage);
        var output = await _session.ReuseAsync(input, token).ConfigureAwait(false);
        if (output is null) return null;
        lock (_gate)
        {
            if (_capturedInput != input) return null;
            if (capture is not null && _capture is not null)
                foreach (var call in _capture.Calls) capture.Add(call);
        }
        Volatile.Write(ref _reused, 1);
        return output;
    }
    public void Dispose() => _session.Dispose();
}
