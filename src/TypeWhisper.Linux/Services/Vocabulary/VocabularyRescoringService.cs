using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services.Vocabulary;

public sealed class VocabularyRescoringService : IVocabularyRescoringService
{
    private static readonly TimeSpan s_rescoreTimeout = TimeSpan.FromSeconds(10);

    // Local Parakeet TDT is the only engine that produces token timings.
    private const string ParakeetModelId = "parakeet-tdt-0.6b";
    private readonly ModelManagerService _models;
    private readonly PluginManager _pluginManager;
    private readonly ISettingsService _settings;
    private readonly IDictionaryService _dictionary;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _draining;

    public VocabularyRescoringService(
        PluginManager pluginManager,
        ISettingsService settings,
        IDictionaryService dictionary,
        ModelManagerService models
    )
        : this(pluginManager, settings, dictionary, models, s_rescoreTimeout) { }

    internal VocabularyRescoringService(
        PluginManager pluginManager,
        ISettingsService settings,
        IDictionaryService dictionary,
        ModelManagerService models,
        TimeSpan timeout
    )
    {
        _models = models;
        _pluginManager = pluginManager;
        _settings = settings;
        _dictionary = dictionary;
        _timeout = timeout;
    }

    public string? ActiveEngineBlocker =>
        _models.ActiveTranscriptionPlugin is { } engine
        && IsParakeet(engine.ProviderId, engine.SelectedModelId)
            ? null
            : "engine";

    private static bool IsParakeet(string? providerId, string? modelId) =>
        providerId == "sherpa-onnx" && modelId == ParakeetModelId;

    public bool IsEligible(
        IReadOnlyList<VocabularyTokenTiming> tokenTimings,
        bool translateRequested,
        string? engineProviderId,
        string? engineModelId
    ) =>
        _settings.Current.AcousticVocabularyBoostingEnabled
        && IsParakeet(engineProviderId, engineModelId)
        && !translateRequested
        && tokenTimings.Count > 0
        && _pluginManager.VocabularyRescorer is { IsReady: true }
        && _dictionary.Entries.Any(IsEnabledTerm);

    private static bool IsEnabledTerm(DictionaryEntry entry) =>
        entry is { IsEnabled: true, EntryType: DictionaryEntryType.Term }
        && !string.IsNullOrWhiteSpace(entry.Original);

    public async Task<VocabularyRescoringOutcome> RefineAsync(
        VocabularyRescoringInput input,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        if (
            !IsEligible(
                input.TokenTimings,
                input.TranslateRequested,
                input.EngineProviderId,
                input.EngineModelId
            )
        )
            return new VocabularyRescoringOutcome(input.Text, false, false, null);
        var samples = PcmWav.ToMonoSamples16K(input.Wav);
        if (samples.Length == 0)
            return new VocabularyRescoringOutcome(input.Text, false, false, null);

        // Like the text booster, a term's Replacement is its canonical spelling and Original an
        // alias, with manual entries outranking packs: the plugin scores every alias and the
        // host writes the canonical spelling.
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var termHints = new Dictionary<string, VocabularyTermHint>(StringComparer.Ordinal);
        foreach (
            var entry in _dictionary
                .Entries.Where(IsEnabledTerm)
                .OrderBy(entry => entry.Id.StartsWith("pack:", StringComparison.Ordinal))
        )
        {
            var output = string.IsNullOrWhiteSpace(entry.Replacement)
                ? entry.Original
                : entry.Replacement.Trim();
            termHints.TryAdd(
                entry.Original,
                new VocabularyTermHint(entry.Original, entry.CtcMinSimilarity)
            );
            termHints.TryAdd(output, new VocabularyTermHint(output, entry.CtcMinSimilarity));
            outputs.TryAdd(entry.Original, output);
            outputs.TryAdd(output, output);
        }
        if (outputs.Count == 0)
            return new VocabularyRescoringOutcome(input.Text, false, false, null);
        var hints = termHints.Values.ToArray();
        var trustedTerms = outputs
            .Values.Distinct(StringComparer.Ordinal)
            .Select(output => new VocabularyTermHint(output))
            .ToArray();
        var request = new VocabularyRescoreRequest(
            input.RecordingId,
            input.Text,
            samples,
            16000,
            Array.AsReadOnly(input.TokenTimings.ToArray()),
            Array.AsReadOnly(hints)
        );
        var timer = Stopwatch.StartNew();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(_timeout);
        var entered = false;
        Task<VocabularyRescoreResult>? pending = null;
        string? errorKind = null;
        try
        {
            // A timed-out decoder still holds the gate; waiting on it again would only
            // stall every later transcription for another full timeout.
            if (Volatile.Read(ref _draining) > 0)
            {
                errorKind = "PluginBusy";
                return new VocabularyRescoringOutcome(
                    input.Text,
                    false,
                    false,
                    "Vocabulary rescoring skipped: previous call still running"
                );
            }
            await _gate.WaitAsync(linked.Token);
            entered = true;
            var plugin = _pluginManager.VocabularyRescorer;
            if (plugin is not { IsReady: true })
                return Discard();
            // A plugin that decodes synchronously before returning its task would otherwise
            // hold the caller and the gate past the timeout.
            pending = Task.Run(
                // ReSharper disable once AccessToDisposedClosure -- linked is disposed only after pending completes: in finally when already complete, otherwise in DrainAsync.
                () => plugin.RescoreAsync(request, linked.Token),
                CancellationToken.None
            );
            var result = await pending.WaitAsync(linked.Token);
            ct.ThrowIfCancellationRequested();
            linked.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(plugin, _pluginManager.VocabularyRescorer) || !plugin.IsReady)
                return Discard();
            var refined = VocabularyRescoreResultValidator.Apply(
                request with
                {
                    Terms = trustedTerms,
                },
                Canonicalize(result, outputs)
            );
            return new VocabularyRescoringOutcome(refined, true, refined != input.Text, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            errorKind = "Cancelled";
            throw;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            errorKind = "Timeout";
            return new VocabularyRescoringOutcome(
                input.Text,
                false,
                false,
                $"Vocabulary rescoring timed out after {_timeout.TotalSeconds:0.#} s"
            );
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            errorKind = ex.GetType().Name;
            // A plugin that withdrew readiness while failing (model missing or incompatible)
            // hands this transcription to the text booster as well.
            return new VocabularyRescoringOutcome(
                input.Text,
                false,
                false,
                "Vocabulary rescoring failed: " + errorKind
            );
        }
        finally
        {
            Trace.WriteLine(
                $"[VocabularyRescoring] samples={samples.Length} timings={request.TokenTimings.Count} terms={hints.Length} elapsedMs={timer.ElapsedMilliseconds} error={errorKind ?? "none"}"
            );
            // Keep serialization while an uncooperative decoder drains after the caller returns.
            if (entered && pending is { IsCompleted: false })
            {
                Interlocked.Increment(ref _draining);
                _ = DrainAsync(pending, linked);
            }
            else
            {
                linked.Dispose();
                if (entered)
                    _gate.Release();
            }
        }

        VocabularyRescoringOutcome Discard()
        {
            errorKind = "PluginUnavailable";
            return new VocabularyRescoringOutcome(
                input.Text,
                false,
                false,
                "Vocabulary rescoring result discarded: plugin unavailable"
            );
        }
    }

    private static VocabularyRescoreResult Canonicalize(
        VocabularyRescoreResult result,
        Dictionary<string, string> outputs
    ) =>
        // ReSharper disable once RedundantAlwaysMatchSubpattern -- third-party plugin output can be null at runtime despite the annotation; the validator reports it.
        result is { Replacements: not null }
            ? result with
            {
                Replacements = result
                    .Replacements.Select(replacement =>
                        // ReSharper disable once RedundantAlwaysMatchSubpattern -- third-party plugin output can be null at runtime despite the annotation; the validator reports it.
                        replacement is { Term: not null }
                        && outputs.TryGetValue(replacement.Term, out var output)
                            ? replacement with { Term = output }
                            : replacement
                    )
                    .ToArray(),
            }
            : result;

    private async Task DrainAsync(
        Task<VocabularyRescoreResult> pending,
        CancellationTokenSource linked
    )
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        finally
        {
            linked.Dispose();
            _gate.Release();
            Interlocked.Decrement(ref _draining);
        }
    }
}
