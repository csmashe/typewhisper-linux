using System.Diagnostics;
using System.Text.RegularExpressions;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.ParakeetCtc;

public sealed partial class ParakeetCtcPlugin : IVocabularyRescorerPlugin
{
    private static readonly TimeSpan s_idleUnload = TimeSpan.FromMinutes(10);
    private readonly TimeSpan _idleUnload;
    private readonly Func<string, NemoCtcModel> _modelFactory;
    private CancellationTokenSource? _idleTimer;
    private string? _modelPath;
    private NemoCtcModel? _model;
    private CtcTokenizer? _tokenizer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _disposed;
    private IPluginHostServices? _host;
    private readonly HttpClient _downloads;
    private readonly Lock _activationSync = new();
    private CancellationTokenSource? _activation;
    private Task _activationCallbacks = Task.CompletedTask;

    public ParakeetCtcPlugin()
        : this(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }) { }

    internal ParakeetCtcPlugin(
        HttpClient downloads,
        TimeSpan? idleUnload = null,
        Func<string, NemoCtcModel>? modelFactory = null
    )
    {
        _downloads = downloads;
        _idleUnload = idleUnload ?? s_idleUnload;
        _modelFactory = modelFactory ?? (path => new NemoCtcModel(path));
    }

    public string PluginId => "com.typewhisper.parakeet-ctc";
    public string PluginName => "Parakeet CTC Vocabulary";
    public string PluginVersion => PluginBuildInfo.Version;
    public bool IsReady => !_disposed && _tokenizer is not null && _modelPath is not null;

    public Task ActivateAsync(IPluginHostServices host) =>
        ActivateAsync(host, CancellationToken.None);

    // ReSharper disable once MemberCanBePrivate.Global -- upstream public API kept for hosts that cancel activation.
    public async Task ActivateAsync(IPluginHostServices host, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var activation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_activationSync)
            _activation = activation;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsReady)
                return;
            var directory =
                host.GetSetting<string>("ModelDirectory")
                ?? Path.Join(host.PluginAssetDirectory, "model");
            if (host.GetSetting<string>("ModelDirectory") is null)
                await new CtcModelAssets(_downloads)
                    .EnsureAsync(
                        directory,
                        activation.Token,
                        message => host.Log(PluginLogLevel.Info, message)
                    )
                    .ConfigureAwait(false);
            activation.Token.ThrowIfCancellationRequested();
            var tokenizer = new CtcTokenizer(Path.Join(directory, "tokens.txt"));
            activation.Token.ThrowIfCancellationRequested();
            _tokenizer = tokenizer;
            _modelPath = Path.Join(directory, "model.int8.onnx");
            _host = host;
        }
        finally
        {
            Task callbacks;
            lock (_activationSync)
            {
                _activation = null;
                callbacks = _activationCallbacks;
                _activationCallbacks = Task.CompletedTask;
            }
            try
            {
                await callbacks.ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        host.NotifyCapabilitiesChanged();
    }

    public async Task<VocabularyRescoreResult> RescoreAsync(
        VocabularyRescoreRequest request,
        CancellationToken cancellationToken
    )
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var ready = IsReady;
        var host = _host;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!ready)
                throw new InvalidOperationException("CTC assets are not ready.");
            CancelIdleTimer();
            if (request.SampleRate != 16000)
                throw new NotSupportedException("CTC requires 16 kHz mono PCM.");
            await LoadModelAsync(cancellationToken).ConfigureAwait(false);
            var result = await Task.Run(
                    () => Rescore(request, cancellationToken),
                    cancellationToken
                )
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            if (_model is not null && !_disposed)
                ResetIdleTimer();
            // A failed load withdrew the capability; the host learns outside the gate as in activation.
            var withdrawn = ready && _modelPath is null && !_disposed;
            _gate.Release();
            if (withdrawn)
                host?.NotifyCapabilitiesChanged();
        }
    }

    private async Task LoadModelAsync(CancellationToken cancellation)
    {
        if (_model is not null)
            return;
        var timer = Stopwatch.StartNew();
        NemoCtcModel? model = null;
        try
        {
            // A session that finished loading stays for the next request even when this one was
            // cancelled meanwhile; the host's rescoring timeout would otherwise repeat the cold
            // load on every call.
            model = await Task.Run(() => _modelFactory(_modelPath!), cancellation)
                .ConfigureAwait(false);
            if (
                model.Metadata.GetValueOrDefault("subsampling_factor")
                    != NemoCtcModel.Subsampling.ToString()
                || model.Metadata.GetValueOrDefault("normalize_type") != "per_feature"
                || _tokenizer!.BlankId != 1024
            )
                throw new NotSupportedException(
                    "Expected the Parakeet 110M CTC export and matching tokens."
                );
        }
        catch (Exception ex)
        {
            model?.Dispose();
            if (ex is OperationCanceledException)
                throw;
            // A missing or incompatible model never recovers on its own; stay unavailable so
            // the host falls back to text boosting until the plugin is re-enabled.
            _modelPath = null;
            _host?.Log(PluginLogLevel.Error, "CTC model failed to load: " + ex.Message);
            throw;
        }
        _model = model;
        _host?.Log(PluginLogLevel.Info, $"CTC model loaded in {timer.ElapsedMilliseconds} ms");
    }

    private void CancelIdleTimer()
    {
        _idleTimer?.Cancel();
        _idleTimer?.Dispose();
        _idleTimer = null;
    }

    private void ResetIdleTimer()
    {
        CancelIdleTimer();
        _idleTimer = new CancellationTokenSource();
        _ = UnloadWhenIdleAsync(_idleTimer.Token);
    }

    private async Task UnloadWhenIdleAsync(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(_idleUnload, cancellation).ConfigureAwait(false);
            await _gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                // A request may have reset the timer while this callback waited for the gate.
                cancellation.ThrowIfCancellationRequested();
                _model?.Dispose();
                _model = null;
                CancelIdleTimer();
                _host?.Log(PluginLogLevel.Info, "CTC model unloaded after idle");
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private VocabularyRescoreResult Rescore(
        VocabularyRescoreRequest request,
        CancellationToken cancellation
    )
    {
        Trace("plugin-enter");
        if (
            request.Audio.Length < 400
            || request.Terms.Count == 0
            || request.TokenTimings.Count == 0
        )
            return Skip("missing-input");
        var duration = request.Audio.Length / 16000d;
        var aligned = new List<(int Start, int End, double From, double To)>();
        var cursor = 0;
        foreach (var timing in request.TokenTimings)
        {
            if (
                !double.IsFinite(timing.StartSeconds)
                || !double.IsFinite(timing.EndSeconds)
                || timing.StartSeconds < 0
                || timing.EndSeconds <= timing.StartSeconds
                || timing.EndSeconds > duration
            )
                return Skip(
                    $"invalid-timing index={aligned.Count} start={timing.StartSeconds:R} end={timing.EndSeconds:R} duration={duration:R}"
                );
            var token = timing.Text.Replace('▁', ' ').Trim();
            if (token.Length == 0)
                continue;
            var position = request.Text.IndexOf(token, cursor, StringComparison.OrdinalIgnoreCase);
            if (position < 0)
                return Skip($"token-text-alignment index={aligned.Count} cursor={cursor}");
            aligned.Add(
                (position, position + token.Length, timing.StartSeconds, timing.EndSeconds)
            );
            cursor = position + token.Length;
        }
        var words = WordRegex().Matches(request.Text).ToArray();
        var candidates =
            new List<(
                int Start,
                int Length,
                string Term,
                string Original,
                double From,
                double To,
                double Similarity
            )>();
        var similarityRejected = 0;
        var timingRejected = 0;
        double bestSimilarity = 0;
        foreach (
            var term in request.Terms.Take(256).DistinctBy(t => t.Text, StringComparer.Ordinal)
        )
        {
            cancellation.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(term.Text) || term.Text.Length > 160)
                continue;
            var threshold =
                term.MinimumSimilarity
                ?? CtcBiasPolicy.MinimumSimilarity(Math.Min(request.Terms.Count, 256));
            if (!float.IsFinite(threshold) || threshold is < 0 or > 1)
                continue;
            for (var first = 0; first < words.Length; first++)
            for (var count = 1; count <= 3 && first + count <= words.Length; count++)
            {
                var start = words[first].Index;
                var end = words[first + count - 1].Index + words[first + count - 1].Length;
                var original = request.Text[start..end];
                var similarity = Similarity(original, term.Text);
                bestSimilarity = Math.Max(bestSimilarity, similarity);
                // Compare at the threshold's float precision: .60f promoted to double sits
                // just above 0.6 and would reject a window at exactly the minimum.
                if (original == term.Text || (float)similarity < threshold)
                {
                    similarityRejected++;
                    continue;
                }
                if (WindowAlreadyContainsTerm(original, term.Text))
                {
                    similarityRejected++;
                    continue;
                }
                var times = aligned.Where(t => t.Start < end && t.End > start).ToArray();
                if (times.Length == 0 || times[0].Start > start || times[^1].End < end)
                {
                    timingRejected++;
                    continue;
                }
                candidates.Add(
                    (
                        start,
                        end - start,
                        term.Text,
                        original,
                        times[0].From,
                        times[^1].To,
                        similarity
                    )
                );
            }
        }
        var proposals =
            new List<(int Start, int Length, string Term, double Similarity, double Score)>();
        Trace(
            $"candidates count={candidates.Count} similarityRejected={similarityRejected} timingRejected={timingRejected} bestSimilarity={bestSimilarity:R} termLimit=256 candidateLimit=64"
        );
        // Cache one bounded emission window at a time; long recordings do not
        // allocate an unbounded [time, vocabulary] matrix.
        int cachedStart = -1,
            cachedEnd = -1;
        CtcEmission? emission = null;
        foreach (var candidate in candidates.OrderBy(c => c.From).Take(64))
        {
            cancellation.ThrowIfCancellationRequested();
            var start = Math.Max(0, (int)((candidate.From - .5) * 16000));
            var end = Math.Min(
                request.Audio.Length,
                (int)Math.Ceiling((candidate.To + .5) * 16000)
            );
            if (end - start is < 400 or > 16000 * 30)
            {
                Trace($"candidate-skipped start={candidate.Start} reason=audio-window");
                continue;
            }
            if (start < cachedStart || end > cachedEnd || emission is null)
            {
                cachedStart = start;
                cachedEnd = Math.Min(request.Audio.Length, start + 16000 * 30);
                emission = _model!.Evaluate(
                    request.Audio.Slice(cachedStart, cachedEnd - cachedStart),
                    cancellation
                );
            }
            var from = (int)((start - cachedStart) / 16000d / emission.FrameSeconds);
            var to = (int)Math.Ceiling((end - cachedStart) / 16000d / emission.FrameSeconds);
            var preferred = Score(emission, from, to, candidate.Term, true, cancellation);
            var original = Score(emission, from, to, candidate.Original, false, cancellation);
            var bonus = CtcBiasPolicy.Bonus(preferred.Tokens);
            var accepted = CtcBiasPolicy.Accept(original.Score, preferred.Score, preferred.Tokens);
            Trace(
                $"candidate start={candidate.Start} length={candidate.Length} originalScore={original.Score:R} preferredScore={preferred.Score:R} tokens={preferred.Tokens} bonus={bonus:R} accepted={accepted}"
            );
            if (accepted)
                proposals.Add(
                    (
                        candidate.Start,
                        candidate.Length,
                        candidate.Term,
                        candidate.Similarity,
                        preferred.Score + bonus - original.Score
                    )
                );
        }
        var selected = SelectReplacements(proposals);
        Trace($"plugin-finish replacements={selected.Count}");
        return new VocabularyRescoreResult(request.RecordingId, selected);
        void Trace(string message) =>
            _host?.Log(PluginLogLevel.Info, $"{request.RecordingId} {message}");
        VocabularyRescoreResult Skip(string reason)
        {
            Trace("plugin-skipped reason=" + reason);
            return new VocabularyRescoreResult(request.RecordingId, []);
        }
    }

    private (double Score, int Tokens) Score(
        CtcEmission emission,
        int from,
        int to,
        string value,
        bool variants,
        CancellationToken cancellation
    )
    {
        // Hyphens separate spoken words but are absent from the model's BPE vocabulary.
        if (variants)
            value = SpokenForm(value);
        var encodings = variants
            ? new[] { _tokenizer!.Encode(value), _tokenizer!.Encode(value, false) }
            : new[] { _tokenizer!.Encode(value) };
        return encodings
            .Select(tokens =>
                (
                    Score: CtcVocabularyScorer.Score(
                        emission,
                        tokens,
                        _tokenizer!.BlankId,
                        from,
                        to,
                        cancellation
                    ),
                    Tokens: tokens.Length
                )
            )
            .OrderByDescending(result => result.Score)
            .First();
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’-][\p{L}\p{N}]+)*")]
    private static partial Regex WordRegex();

    internal static string SpokenForm(string value) =>
        value.Replace('-', ' ').Replace('–', ' ').Replace('—', ' ');

    internal static bool WindowAlreadyContainsTerm(string original, string term) =>
        Regex.IsMatch(original, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}])");

    internal static IReadOnlyList<VocabularyReplacement> SelectReplacements(
        IReadOnlyList<(
            int Start,
            int Length,
            string Term,
            double Similarity,
            double Score
        )> proposals
    )
    {
        var selected = new List<VocabularyReplacement>();
        // The span whose text is closest to the term is the one the term replaces; the
        // acoustic margin only breaks ties.
        foreach (
            var proposal in proposals
                .OrderByDescending(p => p.Similarity)
                .ThenByDescending(p => p.Score)
        )
            if (
                !selected.Any(p =>
                    p.Start < proposal.Start + proposal.Length
                    && proposal.Start < p.Start + p.Length
                )
            )
                selected.Add(
                    new VocabularyReplacement(
                        proposal.Start,
                        proposal.Length,
                        proposal.Term,
                        proposal.Score
                    )
                );
        return selected.OrderBy(p => p.Start).ToArray();
    }

    private static double Similarity(string a, string b)
    {
        a = string.Concat(a.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        b = string.Concat(b.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        if (a.Length == 0 || b.Length == 0 || a.Length > 160)
            return 0;
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var previous = row[j];
                row[j] = Math.Min(
                    Math.Min(row[j] + 1, row[j - 1] + 1),
                    diagonal + (a[i - 1] == b[j - 1] ? 0 : 1)
                );
                diagonal = previous;
            }
        }
        return 1 - row[^1] / (double)Math.Max(a.Length, b.Length);
    }

    public async Task DeactivateAsync()
    {
        lock (_activationSync)
            if (_activation is { IsCancellationRequested: false } activation)
                _activationCallbacks = ObserveCancellationAsync(activation.CancelAsync());
        // Dispose blocks on this method; resuming on the caller's context would deadlock
        // a UI thread while activation or rescoring still holds the gate.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CancelIdleTimer();
            _model?.Dispose();
            _model = null;
            _modelPath = null;
            _tokenizer = null;
            _host = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task ObserveCancellationAsync(Task cancellation)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (AggregateException) { }
    }

    public void Dispose()
    {
        _disposed = true;
        DeactivateAsync().GetAwaiter().GetResult();
        _downloads.Dispose();
    }
}
