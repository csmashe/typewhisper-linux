using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

internal sealed class HttpApiRequestDispatcher
{
    private readonly Action<Exception> _reportException;
    private readonly SemaphoreSlim _slots;

    public HttpApiRequestDispatcher(int capacity, Action<Exception>? reportException = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _slots = new SemaphoreSlim(capacity, capacity);
        _reportException = reportException ?? (ex =>
            Trace.WriteLine($"[HttpApiService] Dispatched request failed: {ex}"));
    }

    public Task? TryRun(Func<Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return _slots.Wait(0) ? RunAsync(handler) : null;
    }

    private async Task RunAsync(Func<Task> handler)
    {
        try
        {
            await handler();
        }
        catch (Exception ex)
        {
            _reportException(ex);
        }
        finally
        {
            _slots.Release();
        }
    }
}

internal sealed record HttpApiOverCapacityResponse(
    int StatusCode,
    string RetryAfter,
    string Body
);

/// <summary>
///     Local HTTP API for dictation/transcription/history. Binds to localhost
///     only; CORS is echoed only for the same loopback origin and port so a
///     remote page cannot induce a localhost-origin request to leak the API.
/// </summary>
public sealed class HttpApiService : IDisposable
{
    internal const int MaxConcurrentRequests = 2;
    internal const long MaxTranscribeRequestBytes = 100 * 1024 * 1024;
    internal const long MaxJsonRequestBytes = 1 * 1024 * 1024;

    private const string AllowedCorsHeaders =
        "Authorization, Content-Type, X-Language, X-Language-Hints, X-Task, X-Target-Language, "
        + "X-Response-Format, X-Prompt, X-Engine, X-Model";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly AudioFileService _audioFiles;
    private readonly DictationOrchestrator _dictation;
    private readonly IDictionaryService _dictionary;
    private readonly ApiDiscoveryFile _discoveryFile;
    private readonly IHistoryService _history;
    private readonly ModelManagerService _models;
    private readonly IPostProcessingPipeline _pipeline;
    private readonly IProfileService _profiles;
    private readonly HttpApiRequestDispatcher _requestDispatcher = new(MaxConcurrentRequests);
    private readonly DictationSessionResultStore _sessionResults;
    private readonly ISettingsService _settings;
    private readonly ITranslationService _translation;
    private readonly IVocabularyBoostingService _vocabularyBoosting;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    private HttpListener? _listener;
    private Task? _listenTask;
    private int _port;

    public HttpApiService(
        ModelManagerService models,
        ISettingsService settings,
        AudioFileService audioFiles,
        IHistoryService history,
        IProfileService profiles,
        IDictionaryService dictionary,
        IVocabularyBoostingService vocabularyBoosting,
        IPostProcessingPipeline pipeline,
        ITranslationService translation,
        DictationOrchestrator dictation,
        DictationSessionResultStore sessionResults,
        ApiDiscoveryFile discoveryFile
    )
    {
        _models = models;
        _settings = settings;
        _audioFiles = audioFiles;
        _history = history;
        _profiles = profiles;
        _dictionary = dictionary;
        _vocabularyBoosting = vocabularyBoosting;
        _pipeline = pipeline;
        _translation = translation;
        _dictation = dictation;
        _sessionResults = sessionResults;
        _discoveryFile = discoveryFile;
    }

    public string StatusText { get; private set; } = "Local API is disabled.";

    private bool IsRunning => _listener?.IsListening == true;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _cts?.Dispose();
        try
        {
            _listenTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Best-effort wait for the listener loop to drain during dispose.
        }

        _disposed = true;
    }

    private void Start(int port)
    {
        if (IsRunning && _port == port)
        {
            SetStatus($"Local API is running at http://localhost:{port}/");
            return;
        }

        if (port is <= 0 or > 65535)
        {
            Stop(false);
            SetStatus($"Local API failed to start: port must be 1–65535 (got {port}).");
            return;
        }

        Stop(false);

        try
        {
            _port = port;
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));

            var token = ReadBearerToken(_settings.Current);
            if (!string.IsNullOrWhiteSpace(token))
            {
                _discoveryFile.Write(port, token);
            }

            SetStatus($"Local API is running at http://localhost:{port}/");
        }
        catch (Exception ex)
        {
            Stop(false);
            SetStatus($"Local API failed to start: {ex.Message}");
        }
    }

    private void Stop()
    {
        // ReSharper disable once IntroduceOptionalParameters.Local -- kept as explicit overloads; collapsing into an optional parameter would delete a member.
        Stop(true);
    }

    public void ApplySettings()
    {
        var settings = _settings.Current;
        if (settings.ApiServerEnabled)
        {
            EnsureBearerToken();
            Start(_settings.Current.ApiServerPort);
        }
        else
        {
            Stop();
        }
    }

    internal static string ReadBearerToken(AppSettings settings)
    {
        return string.IsNullOrWhiteSpace(settings.ApiServerBearerToken)
            ? ""
            : ApiKeyProtection.Decrypt(settings.ApiServerBearerToken);
    }

    internal static object? BuildAccelerationDto(
        ITranscriptionEnginePlugin? plugin,
        AppSettings settings
    )
    {
        if (plugin?.AccelerationStatus is not { } status)
        {
            return null;
        }

        return new
        {
            preference = AppSettings.NormalizeLocalModelAcceleration(settings.LocalModelAcceleration),
            activeBackend = FormatAccelerationBackend(status.ActiveBackend),
            displayText = status.DisplayText,
            detail = status.Detail,
            requiresRestart = status.RequiresRestart,
        };
    }

    public event Action? StateChanged;

    private void Stop(bool updateStatus)
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        _listener = null;
        _port = 0;
        _discoveryFile.Delete();
        if (updateStatus)
        {
            SetStatus("Local API is disabled.");
        }
    }

    private void SetStatus(string status)
    {
        if (StatusText == status)
        {
            return;
        }

        StatusText = status;
        StateChanged?.Invoke();
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true } listener)
        {
            try
            {
                var context = await listener.GetContextAsync();
                var handlerTask = _requestDispatcher.TryRun(() =>
                    HandleRequestAsync(context, ct)
                );
                if (handlerTask is null)
                {
                    // Fire-and-forget like the admitted path: awaiting the rejection
                    // would let one slow client stall accepts. The method swallows its
                    // own exceptions and closes the response.
                    _ = RejectOverCapacityAsync(context, ct);
                }
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                // Keep the local API alive after malformed requests.
            }
        }
    }

    internal static HttpApiOverCapacityResponse CreateOverCapacityResponse()
    {
        return new HttpApiOverCapacityResponse(
            (int)HttpStatusCode.TooManyRequests,
            "1",
            Serialize(new { error = "Too many concurrent requests" })
        );
    }

    private async Task RejectOverCapacityAsync(
        HttpListenerContext context,
        CancellationToken ct
    )
    {
        var response = context.Response;
        try
        {
            var rejection = CreateOverCapacityResponse();
            response.Headers["Retry-After"] = rejection.RetryAfter;
            await WriteJsonAsync(
                response,
                rejection.StatusCode,
                rejection.Body,
                GetAllowedOrigin(context.Request),
                ct
            );
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[HttpApiService] Over-capacity response failed: {ex}");
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch
            {
                // Best-effort close for disconnected overload clients.
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var response = context.Response;
        try
        {
            var request = context.Request;
            var path = request.Url?.AbsolutePath ?? "";
            var method = request.HttpMethod;
            var allowedOrigin = GetAllowedOrigin(request);

            // CORS preflight: respond before auth so browsers can complete the handshake.
            if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(allowedOrigin))
                {
                    response.Headers["Access-Control-Allow-Origin"] = allowedOrigin;
                    response.Headers["Access-Control-Allow-Methods"] =
                        "GET, POST, PUT, DELETE, OPTIONS";
                    response.Headers["Access-Control-Allow-Headers"] = AllowedCorsHeaders;
                    response.Headers["Access-Control-Max-Age"] = "600";
                }

                response.StatusCode = 204;
                response.ContentLength64 = 0;
                return;
            }

            if (!IsAuthorized(request))
            {
                response.Headers["WWW-Authenticate"] = "Bearer";
                // Include CORS so browser clients from allowed loopback origins can read the 401.
                await WriteJsonAsync(
                    response,
                    401,
                    Serialize(new { error = "Unauthorized" }),
                    allowedOrigin,
                    ct
                );
                return;
            }

            if (!IsValidOrigin(request) || !IsAllowedLoopbackHost(request.Url?.Host))
            {
                // Origin itself is forbidden — do not send CORS to it.
                await WriteJsonAsync(
                    response,
                    403,
                    Serialize(new { error = "Forbidden" }),
                    null,
                    ct
                );
                return;
            }

            var (statusCode, body) = (path, method) switch
            {
                ("/v1/status", "GET") => HandleStatus(),
                ("/v1/models", "GET") => HandleModels(),
                ("/v1/transcribe", "POST") => await HandleTranscribeAsync(request, ct),
                ("/v1/transcribe/local-file", "POST") =>
                    await HandleTranscribeLocalFileAsync(request, ct),
                ("/v1/history", "GET") => HandleHistorySearch(request),
                ("/v1/history", "DELETE") => HandleHistoryDelete(request),
                ("/v1/profiles", "GET") => HandleProfilesList(),
                ("/v1/profiles/toggle", "PUT") => HandleProfileToggle(request),
                ("/v1/dictation/start", "POST") => await HandleDictationStartAsync(),
                ("/v1/dictation/stop", "POST") => await HandleDictationStopAsync(),
                ("/v1/dictation/status", "GET") => HandleDictationStatus(),
                ("/v1/dictation/transcription", "GET") => HandleDictationTranscription(request),
                ("/v1/dictionary/terms", "GET") => HandleGetDictionaryTerms(),
                ("/v1/dictionary/terms", "PUT") => await HandlePutDictionaryTermsAsync(request, ct),
                ("/v1/dictionary/terms", "DELETE") =>
                    await HandleDeleteDictionaryTermAsync(request, ct),
                ("/v1/dictionary/corrections", "GET") => HandleGetDictionaryCorrections(),
                ("/v1/dictionary/corrections", "PUT") =>
                    await HandlePutDictionaryCorrectionAsync(request, ct),
                ("/v1/dictionary/corrections", "DELETE") =>
                    await HandleDeleteDictionaryCorrectionAsync(request, ct),
                _ => (404, Serialize(new { error = "Not found" })),
            };

            await WriteJsonAsync(response, statusCode, body, allowedOrigin, ct);
        }
        catch (HttpApiRequestException ex)
        {
            await WriteJsonAsync(
                response,
                ex.StatusCode,
                Serialize(new { error = ex.Message }),
                GetAllowedOrigin(context.Request),
                ct
            );
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[HttpApiService] Request failed: {ex}");
            // Re-resolve origin: allowedOrigin may not be in scope if the exception
            // was thrown before it was computed.
            var recoveredOrigin = GetAllowedOrigin(context.Request);
            await WriteJsonAsync(
                response,
                500,
                Serialize(new { error = "Internal server error" }),
                recoveredOrigin,
                ct
            );
        }
        finally
        {
            response.Close();
        }
    }

    private (int, string) HandleStatus()
    {
        var plugin = _models.ActiveTranscriptionPlugin;
        var activeModel =
            _models.ActiveModelId is { } activeModelId
            && ModelManagerService.IsPluginModel(activeModelId)
                ? ModelManagerService.ParsePluginModelId(activeModelId).ModelId
                : plugin?.SelectedModelId;
        return (
            200,
            Serialize(
                new
                {
                    status = plugin is not null ? "ready" : "no_model",
                    engine = plugin?.ProviderId,
                    model = activeModel,
                    activeModel = _models.ActiveModelId,
                    apiVersion = "1.0",
                    supportsStreaming = plugin?.SupportsStreaming ?? false,
                    supportsTranslation = plugin?.SupportsTranslation ?? false,
                    acceleration = BuildAccelerationDto(plugin, _settings.Current),
                }
            )
        );
    }

    private static string FormatAccelerationBackend(TranscriptionAccelerationBackend backend)
    {
        return backend switch
        {
            TranscriptionAccelerationBackend.NvidiaCuda => "nvidia-cuda",
            _ => "cpu",
        };
    }

    private (int, string) HandleModels()
    {
        var models = _models.PluginManager.TranscriptionEngines.SelectMany(engine =>
            engine.TranscriptionModels.Select(model =>
            {
                var id = ModelManagerService.GetPluginModelId(engine.GetTranscriptionSelectionId(), model.Id);
                return new
                {
                    id = model.Id,
                    fullId = id,
                    name = $"{engine.ProviderDisplayName}: {model.DisplayName}",
                    sizeDescription = model.SizeDescription
                                      ?? (engine.SupportsModelDownload ? "Local" : "Cloud"),
                    engine = engine.ProviderId,
                    downloaded = _models.IsDownloaded(id),
                    selected = _settings.Current.SelectedModelId == id,
                    active = _models.ActiveModelId == id,
                    status = _models.IsDownloaded(id) ? "ready"
                        : engine.SupportsModelDownload ? "not_downloaded"
                        : "not_configured",
                };
            })
        );

        return (200, Serialize(new { models }));
    }

    private async Task<(int, string)> HandleTranscribeAsync(
        HttpListenerRequest request,
        CancellationToken ct
    )
    {
        // Empty body — answer with the same contract ParseTranscribe would produce.
        if (request.ContentLength64 == 0)
        {
            return (400, Serialize(new { error = "No audio data provided" }));
        }

        var prepared = await PrepareTranscriptionRequestAsync(request, ct);
        try
        {
            return await RunTranscriptionAsync(prepared.TempPath, prepared.Options, ct);
        }
        finally
        {
            DeleteTemporaryFileBestEffort(prepared.TempPath);
        }
    }

    private static async Task<PreparedTranscriptionRequest> PrepareTranscriptionRequestAsync(
        HttpListenerRequest request,
        CancellationToken ct
    )
    {
        var apiRequest = await HttpApiRequestParser.FromListenerRequestAsync(
            request,
            MaxTranscribeRequestBytes,
            ct
        );
        var transcribeRequest = HttpApiRequestParser.ParseTranscribe(apiRequest);

        var tempPath = Path.Join(
            Path.GetTempPath(),
            $"typewhisper-api-{Guid.NewGuid():N}.{SanitizeExtension(transcribeRequest.FileExtension)}"
        );
        try
        {
            await File.WriteAllBytesAsync(tempPath, transcribeRequest.AudioData, ct);
            var opts = new TranscriptionRunOptions(
                transcribeRequest.Language,
                transcribeRequest.LanguageHints,
                transcribeRequest.Task,
                transcribeRequest.TargetLanguage,
                transcribeRequest.ResponseFormat,
                transcribeRequest.Prompt,
                transcribeRequest.Engine,
                transcribeRequest.Model,
                transcribeRequest.AwaitDownload
            );
            return new PreparedTranscriptionRequest(tempPath, opts);
        }
        catch
        {
            DeleteTemporaryFileBestEffort(tempPath);
            throw;
        }
    }

    private static void DeleteTemporaryFileBestEffort(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch
        {
            // Best-effort temp-file cleanup.
        }
    }

    private async Task<(int, string)> HandleTranscribeLocalFileAsync(
        HttpListenerRequest request,
        CancellationToken ct
    )
    {
        var apiRequest = await HttpApiRequestParser.FromListenerRequestAsync(
            request,
            MaxJsonRequestBytes,
            ct
        );
        if (apiRequest.Body.Length == 0)
        {
            return (400, Serialize(new { error = "Missing JSON body" }));
        }

        LocalFileTranscribeRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LocalFileTranscribeRequest>(
                apiRequest.Body.Span,
                s_jsonOptions
            );
        }
        catch (JsonException)
        {
            return (400, Serialize(new { error = "Invalid JSON body" }));
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Path))
        {
            return (400, Serialize(new { error = "Missing required field: path" }));
        }

        if (!File.Exists(payload.Path))
        {
            return (400, Serialize(new { error = "File not found" }));
        }

        if (!AudioFileService.IsSupported(payload.Path))
        {
            return (400, Serialize(new { error = "Unsupported format" }));
        }

        var task = string.Equals(payload.Task, "translate", StringComparison.OrdinalIgnoreCase)
            ? TranscriptionTask.Translate
            : TranscriptionTask.Transcribe;
        var opts = new TranscriptionRunOptions(
            payload.Language,
            // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract -- LanguageHints is deserialized from JSON and can be null when the field is omitted
            payload.LanguageHints ?? [],
            task,
            payload.TargetLanguage,
            string.IsNullOrWhiteSpace(payload.ResponseFormat) ? "json" : payload.ResponseFormat,
            payload.Prompt,
            payload.Engine,
            payload.Model,
            payload.AwaitDownload
        );
        return await RunTranscriptionAsync(payload.Path, opts, ct);
    }

    private async Task<(int, string)> RunTranscriptionAsync(
        string audioPath,
        TranscriptionRunOptions opts,
        CancellationToken ct
    )
    {
        var modelId = ResolveRequestedModelId(opts.Engine, opts.Model);

        // Refuse to block on a model download unless the caller opted in via
        // await_download=1 — otherwise the CLI's 5-min budget would be consumed.
        if (!opts.AwaitDownload)
        {
            var resolvedModelId = modelId ?? _settings.Current.SelectedModelId;
            if (
                !string.IsNullOrWhiteSpace(resolvedModelId)
                && !_models.IsDownloaded(resolvedModelId)
            )
            {
                return (
                    503,
                    Serialize(
                        new { error = "Model is not downloaded. Pass await_download=1 to wait for the download." }
                    )
                );
            }
        }

        // Decode audio before acquiring the lease — ffmpeg shells out and
        // must not hold the model lock while no transcription runs.
        var wav = await _audioFiles.LoadAudioAsWavAsync(audioPath, ct);
        var settings = _settings.Current;
        var language = opts.Language ?? (settings.Language == "auto" ? null : settings.Language);
        var prompt = MergePrompt(
            opts.Prompt,
            BuildLanguageHintsPrompt(opts.LanguageHints),
            _dictionary.GetTermsForPrompt()
        );

        // Hold the lease only around TranscribeAsync so no concurrent caller
        // can swap the shared plugin's model mid-run.
        PluginTranscriptionResult result;
        string engineProviderId;
        string? selectedModelId;

        ModelManagerService.TranscriptionLease lease;
        try
        {
            lease = await _models.AcquireTranscriptionAsync(modelId, cancellationToken: ct);
        }
        catch (InvalidOperationException)
        {
            return (503, Serialize(new { error = "No model loaded" }));
        }

        await using (lease)
        {
            var plugin = lease.Plugin;
            result = await plugin.TranscribeAsync(
                wav,
                language,
                opts.Task == TranscriptionTask.Translate,
                prompt,
                ct
            );
            engineProviderId = plugin.ProviderId;
            selectedModelId = plugin.SelectedModelId;
        }

        var processed = await _pipeline.ProcessAsync(
            result.Text,
            new PipelineOptions
            {
                VocabularyBooster = settings.VocabularyBoostingEnabled
                    ? _vocabularyBoosting.Apply
                    : null,
                DictionaryCorrector = _dictionary.ApplyCorrections,
            },
            ct
        );

        var finalText = processed.Text;
        if (!string.IsNullOrWhiteSpace(opts.TargetLanguage))
        {
            try
            {
                finalText = await _translation.TranslateAsync(
                    finalText,
                    result.DetectedLanguage ?? language ?? "en",
                    opts.TargetLanguage,
                    ct: ct
                );
            }
            catch (NotSupportedException ex)
            {
                return (501, Serialize(new { error = ex.Message }));
            }
            catch (InvalidOperationException ex)
            {
                return (501, Serialize(new { error = ex.Message }));
            }
        }

        if (opts.ResponseFormat.Equals("verbose_json", StringComparison.OrdinalIgnoreCase))
        {
            return (
                200,
                Serialize(
                    new
                    {
                        text = finalText,
                        language = result.DetectedLanguage,
                        duration = result.DurationSeconds,
                        noSpeechProbability = result.NoSpeechProbability,
                        engine = engineProviderId,
                        model = selectedModelId,
                        segments = result.Segments.Select(segment => new
                        {
                            text = segment.Text, start = segment.Start, end = segment.End,
                        }),
                    }
                )
            );
        }

        return (
            200,
            Serialize(
                new
                {
                    text = finalText,
                    language = result.DetectedLanguage,
                    duration = result.DurationSeconds,
                    noSpeechProbability = result.NoSpeechProbability,
                    engine = engineProviderId,
                    model = selectedModelId,
                }
            )
        );
    }

    private (int, string) HandleHistorySearch(HttpListenerRequest request)
    {
        var query = request.QueryString["q"] ?? "";
        var limit = int.TryParse(request.QueryString["limit"], out var parsedLimit)
            ? parsedLimit
            : 50;
        var offset = int.TryParse(request.QueryString["offset"], out var parsedOffset)
            ? parsedOffset
            : 0;

        var records = string.IsNullOrWhiteSpace(query) ? _history.Records : _history.Search(query);

        var paged = records
            .Skip(offset)
            .Take(limit)
            .Select(record => new
            {
                id = record.Id,
                timestamp = record.Timestamp.ToString("O"),
                text = record.FinalText,
                rawText = record.RawText,
                app = record.AppProcessName,
                duration = record.DurationSeconds,
                language = record.Language,
                engine = record.EngineUsed,
                model = record.ModelUsed,
                profile = record.ProfileName,
                words = record.WordCount,
            });

        return (
            200,
            Serialize(
                new { total = records.Count, offset, limit, records = paged }
            )
        );
    }

    private (int, string) HandleHistoryDelete(HttpListenerRequest request)
    {
        var id = request.QueryString["id"];
        if (string.IsNullOrWhiteSpace(id))
        {
            return (400, Serialize(new { error = "Missing id parameter" }));
        }

        _history.DeleteRecord(id);
        return (200, Serialize(new { deleted = true, id }));
    }

    private (int, string) HandleProfilesList()
    {
        var profiles = _profiles.Profiles.Select(profile => new
        {
            id = profile.Id,
            name = profile.Name,
            isEnabled = profile.IsEnabled,
            priority = profile.Priority,
            processNames = profile.ProcessNames,
            urlPatterns = profile.UrlPatterns,
            inputLanguage = profile.InputLanguage,
            translationTarget = profile.TranslationTarget,
            selectedTask = profile.SelectedTask,
            modelOverride = profile.TranscriptionModelOverride,
            promptActionId = profile.PromptActionId,
        });

        return (200, Serialize(new { profiles }));
    }

    private (int, string) HandleProfileToggle(HttpListenerRequest request)
    {
        var id = request.QueryString["id"];
        if (string.IsNullOrWhiteSpace(id))
        {
            return (400, Serialize(new { error = "Missing id parameter" }));
        }

        var profile = _profiles.ToggleProfileEnabled(id);
        if (profile is null)
        {
            return (404, Serialize(new { error = "Profile not found" }));
        }

        var isEnabled = profile.IsEnabled;
        return (200, Serialize(new { id, isEnabled }));
    }

    private async Task<(int, string)> HandleDictationStartAsync()
    {
        if (_dictation.IsRecording)
        {
            return (409, Serialize(new { error = "Already recording" }));
        }

        var sessionId = await _dictation.StartAsync();

        // Orchestrator can bail silently (no device, load failure, gate held);
        // reflect actual state in the response.
        if (!_dictation.IsRecording)
        {
            return (409, Serialize(new { error = "Failed to start dictation" }));
        }

        // sessionId <= 0 means this call didn't allocate the session — a concurrent
        // start or hotkey toggle already owned the gate. Return 409 rather than
        // handing back sessionId: 0 that a polling client would never resolve.
        if (sessionId <= 0)
        {
            return (
                409,
                Serialize(new { error = "Another dictation start is already in progress" })
            );
        }

        return (200, Serialize(new { started = true, sessionId }));
    }

    private (int, string) HandleDictationTranscription(HttpListenerRequest request)
    {
        var sessionIdRaw = request.QueryString["sessionId"];
        if (string.IsNullOrWhiteSpace(sessionIdRaw) || !int.TryParse(sessionIdRaw, out var sessionId))
        {
            return (400, Serialize(new { error = "Missing or invalid sessionId" }));
        }

        if (_sessionResults.TryGet(sessionId, out var stored))
        {
            return (
                200,
                Serialize(
                    new
                    {
                        state = stored.Status,
                        text = stored.Text,
                        rawText = stored.RawText,
                        language = stored.Language,
                        durationSeconds = stored.DurationSeconds,
                        engine = stored.EngineUsed,
                        model = stored.ModelUsed,
                        message = stored.Message,
                    }
                )
            );
        }

        return _dictation.IsSessionInFlight(sessionId)
            ? (200, Serialize(new { state = "in_progress" }))
            : (200, Serialize(new { state = "not_found" }));
    }

    private async Task<(int, string)> HandleDictationStopAsync()
    {
        if (!_dictation.IsRecording)
        {
            return (409, Serialize(new { error = "Not recording" }));
        }

        await _dictation.StopAsync();

        return _dictation.IsRecording
            ? (409, Serialize(new { error = "Failed to stop dictation" }))
            : (200, Serialize(new { stopped = true }));
    }

    private (int, string) HandleDictationStatus()
    {
        return (
            200,
            Serialize(
                new
                {
                    state = _dictation.IsRecording ? "recording" : "idle",
                    isRecording = _dictation.IsRecording,
                    activeModel = _models.ActiveModelId,
                }
            )
        );
    }

    private (int, string) HandleGetDictionaryTerms()
    {
        var terms = _dictionary.GetEnabledTerms();
        return (200, Serialize(new { terms, count = terms.Count }));
    }

    private async Task<(int, string)> HandlePutDictionaryTermsAsync(
        HttpListenerRequest request,
        CancellationToken ct
    )
    {
        var apiRequest = await HttpApiRequestParser.FromListenerRequestAsync(
            request,
            MaxJsonRequestBytes,
            ct
        );
        if (apiRequest.Body.Length == 0)
        {
            return (400, Serialize(new { error = "Missing JSON body" }));
        }

        DictionaryTermsRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<DictionaryTermsRequest>(
                apiRequest.Body.Span,
                s_jsonOptions
            );
        }
        catch (JsonException)
        {
            return (400, Serialize(new { error = "Invalid JSON body" }));
        }

        if (payload is null)
        {
            return (400, Serialize(new { error = "Invalid JSON body" }));
        }

        _dictionary.SetTerms(payload.Terms, payload.Replace ?? false);
        var terms = _dictionary.GetEnabledTerms();
        return (200, Serialize(new { terms, count = terms.Count }));
    }

    private async Task<(int, string)> HandleDeleteDictionaryTermAsync(
        HttpListenerRequest request,
        CancellationToken ct
    )
    {
        var apiRequest = await HttpApiRequestParser.FromListenerRequestAsync(
            request,
            MaxJsonRequestBytes,
            ct
        );
        if (apiRequest.Body.Length == 0)
        {
            return (400, Serialize(new { error = "Missing JSON body: { \"term\": \"...\" }" }));
        }

        DictionaryTermDeleteRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<DictionaryTermDeleteRequest>(
                apiRequest.Body.Span,
                s_jsonOptions
            );
        }
        catch (JsonException)
        {
            return (400, Serialize(new { error = "Invalid JSON body" }));
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Term))
        {
            return (400, Serialize(new { error = "Missing required field: term" }));
        }

        var deleted = _dictionary.DeleteTerm(payload.Term);
        var count = _dictionary.GetEnabledTerms().Count;
        return (200, Serialize(new { deleted, term = payload.Term, count }));
    }

    private (int, string) HandleGetDictionaryCorrections()
    {
        var corrections = _dictionary.GetCorrections();
        return (
            200,
            Serialize(
                new
                {
                    corrections = corrections.Select(c => new
                    {
                        original = c.Original, replacement = c.Replacement, caseSensitive = c.CaseSensitive,
                    }),
                    count = corrections.Count,
                }
            )
        );
    }

    private async Task<(int, string)> HandlePutDictionaryCorrectionAsync(
        HttpListenerRequest request,
        CancellationToken ct
    )
    {
        var apiRequest = await HttpApiRequestParser.FromListenerRequestAsync(
            request,
            MaxJsonRequestBytes,
            ct
        );
        if (apiRequest.Body.Length == 0)
        {
            return (400, Serialize(new { error = "Missing JSON body" }));
        }

        CorrectionUpsertRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CorrectionUpsertRequest>(
                apiRequest.Body.Span,
                s_jsonOptions
            );
        }
        catch (JsonException)
        {
            return (400, Serialize(new { error = "Invalid JSON body" }));
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Original))
        {
            return (400, Serialize(new { error = "Missing required field: original" }));
        }

        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- Replacement is deserialized from JSON and can be null when the field is omitted
        if (payload.Replacement is null)
        {
            return (400, Serialize(new { error = "Missing required field: replacement" }));
        }

        _dictionary.UpsertCorrection(
            payload.Original,
            payload.Replacement,
            payload.CaseSensitive ?? false
        );
        var corrections = _dictionary.GetCorrections();
        return (
            200,
            Serialize(
                new
                {
                    corrections = corrections.Select(c => new
                    {
                        original = c.Original, replacement = c.Replacement, caseSensitive = c.CaseSensitive,
                    }),
                    count = corrections.Count,
                }
            )
        );
    }

    private async Task<(int, string)> HandleDeleteDictionaryCorrectionAsync(
        HttpListenerRequest request,
        CancellationToken ct
    )
    {
        var apiRequest = await HttpApiRequestParser.FromListenerRequestAsync(
            request,
            MaxJsonRequestBytes,
            ct
        );
        if (apiRequest.Body.Length == 0)
        {
            return (400, Serialize(new { error = "Missing JSON body" }));
        }

        CorrectionDeleteRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CorrectionDeleteRequest>(
                apiRequest.Body.Span,
                s_jsonOptions
            );
        }
        catch (JsonException)
        {
            return (400, Serialize(new { error = "Invalid JSON body" }));
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Original))
        {
            return (400, Serialize(new { error = "Missing required field: original" }));
        }

        var deleted = _dictionary.DeleteCorrection(payload.Original);
        var corrections = _dictionary.GetCorrections();
        return (
            200,
            Serialize(
                new
                {
                    deleted,
                    corrections = corrections.Select(c => new
                    {
                        original = c.Original, replacement = c.Replacement, caseSensitive = c.CaseSensitive,
                    }),
                    count = corrections.Count,
                }
            )
        );
    }

    private string? ResolveRequestedModelId(string? requestedEngine, string? requestedModel)
    {
        if (
            !string.IsNullOrWhiteSpace(requestedModel)
            && ModelManagerService.IsPluginModel(requestedModel)
        )
        {
            return requestedModel;
        }

        if (!string.IsNullOrWhiteSpace(requestedEngine))
        {
            var engine = _models.PluginManager.TranscriptionEngines.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.ProviderId,
                    requestedEngine,
                    StringComparison.OrdinalIgnoreCase
                )
                || string.Equals(
                    candidate.PluginId,
                    requestedEngine,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (engine is null)
            {
                throw new HttpApiRequestException(404, $"Unknown engine: {requestedEngine}");
            }

            var model = string.IsNullOrWhiteSpace(requestedModel)
                ? engine.SelectedModelId ?? (engine.TranscriptionModels.Count > 0 ? engine.TranscriptionModels[0] : null)?.Id
                : requestedModel;
            if (
                string.IsNullOrWhiteSpace(model)
                || engine.TranscriptionModels.All(candidate => candidate.Id != model)
            )
            {
                throw new HttpApiRequestException(
                    404,
                    $"Unknown model for engine {requestedEngine}: {requestedModel}"
                );
            }

            return ModelManagerService.GetPluginModelId(engine.GetTranscriptionSelectionId(), model);
        }

        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            return _settings.Current.SelectedModelId;
        }

        // A bare model id is no longer globally unique: multiple engines/profiles
        // can advertise the same id. Don't silently route to the first match —
        // require the caller to disambiguate with an explicit engine.
        var matches = _models
            .PluginManager.TranscriptionEngines.Where(candidate =>
                candidate.TranscriptionModels.Any(model => model.Id == requestedModel)
            )
            .ToList();
        return matches.Count switch
        {
            0 => throw new HttpApiRequestException(404, $"Unknown model: {requestedModel}"),
            > 1 => throw new HttpApiRequestException(
                400,
                $"Ambiguous model '{requestedModel}': provided by multiple engines. "
                    + "Specify the engine explicitly or use the full plugin-qualified model id."
            ),
            _ => ModelManagerService.GetPluginModelId(matches[0].GetTranscriptionSelectionId(), requestedModel),
        };
    }

    private static async Task WriteJsonAsync(
        HttpListenerResponse response,
        int statusCode,
        string body,
        string? origin,
        CancellationToken ct
    )
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        if (!string.IsNullOrWhiteSpace(origin))
        {
            response.Headers["Access-Control-Allow-Origin"] = origin;
            response.Headers["Access-Control-Allow-Headers"] = AllowedCorsHeaders;
        }

        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct);
    }

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, s_jsonOptions);
    }

    private static string SanitizeExtension(string extension)
    {
        var clean = extension.Trim().TrimStart('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(clean) || clean.Any(c => !char.IsLetterOrDigit(c))
            ? "wav"
            : clean;
    }

    private static string? BuildLanguageHintsPrompt(IReadOnlyList<string> languageHints)
    {
        return languageHints.Count == 0
            ? null
            : $"Likely spoken languages: {string.Join(", ", languageHints)}.";
    }

    private static string? MergePrompt(params string?[] parts)
    {
        var merged = string.Join(
            Environment.NewLine,
            parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim())
        );
        return string.IsNullOrWhiteSpace(merged) ? null : merged;
    }

    private void EnsureBearerToken()
    {
        var current = _settings.Current;
        var storedToken = current.ApiServerBearerToken;
        var decryptedToken = ReadBearerToken(current);
        if (!string.IsNullOrWhiteSpace(decryptedToken))
        {
            // Token exists. storedToken == decryptedToken only when stored as plaintext
            // by an older build (Decrypt is a no-op on non-base64 blobs) — re-encrypt
            // on the way through so the stored value is always at-rest protected.
            if (!string.Equals(storedToken, decryptedToken, StringComparison.Ordinal))
            {
                return;
            }

            _settings.Save(
                current with { ApiServerBearerToken = ApiKeyProtection.Encrypt(decryptedToken) }
            );
            return;
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _settings.Save(current with { ApiServerBearerToken = ApiKeyProtection.Encrypt(token) });
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var expectedToken = ReadBearerToken(_settings.Current);
        if (string.IsNullOrWhiteSpace(expectedToken))
        {
            return false;
        }

        var authorization = request.Headers["Authorization"];
        if (
            string.IsNullOrWhiteSpace(authorization)
            || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)
        )
        {
            return false;
        }

        var providedToken = authorization["Bearer ".Length..].Trim();
        // Length short-circuit avoids allocating mismatched byte arrays;
        // FixedTimeEquals then gives constant-time comparison for same-length inputs.
        if (providedToken.Length != expectedToken.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(providedToken),
            Encoding.UTF8.GetBytes(expectedToken)
        );
    }

    private string? GetAllowedOrigin(HttpListenerRequest request)
    {
        var origin = request.Headers["Origin"];
        if (string.IsNullOrWhiteSpace(origin))
        {
            return null;
        }

        // Only echo origins on the same loopback address and port — prevents
        // a cross-origin page from claiming a localhost origin.
        if (
            Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            && IsAllowedLoopbackHost(originUri.Host)
            && originUri.Port == _port
        )
        {
            return origin;
        }

        return null;
    }

    private bool IsValidOrigin(HttpListenerRequest request)
    {
        var origin = request.Headers["Origin"];
        return string.IsNullOrWhiteSpace(origin)
            || string.Equals(origin, GetAllowedOrigin(request), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
               || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record TranscriptionRunOptions(
        string? Language,
        IReadOnlyList<string> LanguageHints,
        TranscriptionTask Task,
        string? TargetLanguage,
        string ResponseFormat,
        string? Prompt,
        string? Engine,
        string? Model,
        bool AwaitDownload
    );

    private sealed record PreparedTranscriptionRequest(
        string TempPath,
        TranscriptionRunOptions Options
    );
}

internal sealed record DictionaryTermsRequest(IReadOnlyList<string> Terms, bool? Replace);
