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

/// <summary>
///     Local HTTP API exposing dictation/transcription/history endpoints to
///     CLI tools and browser scripts. Binds only to <c>http://localhost/</c>
///     prefixes (loopback host + loopback origin checks) — there is no
///     plaintext-over-LAN deployment story for the bearer auth scheme used
///     here. CORS is echoed only for the listener's own loopback origin and
///     port, so a remote page cannot induce a localhost-origin request to
///     leak the API.
/// </summary>
public sealed class HttpApiService : IDisposable
{
    private const long MaxTranscribeRequestBytes = 100 * 1024 * 1024;

    private const string AllowedCorsHeaders =
        "Authorization, Content-Type, X-Language, X-Language-Hints, X-Task, X-Target-Language, "
        + "X-Response-Format, X-Prompt, X-Engine, X-Model";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly AudioFileService _audioFiles;
    private readonly DictationOrchestrator _dictation;
    private readonly DictationSessionResultStore _sessionResults;
    private readonly IDictionaryService _dictionary;
    private readonly ApiDiscoveryFile _discoveryFile;
    private readonly IHistoryService _history;
    private readonly ModelManagerService _models;
    private readonly IPostProcessingPipeline _pipeline;
    private readonly IProfileService _profiles;
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

    public bool IsRunning => _listener?.IsListening == true;

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
        catch { }

        _disposed = true;
    }

    public void Start(int port)
    {
        if (IsRunning && _port == port)
        {
            SetStatus($"Local API is running at http://localhost:{port}/");
            return;
        }

        if (port <= 0 || port > 65535)
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

    public void Stop()
    {
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
                _ = Task.Run(() => HandleRequestAsync(context, ct), ct);
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

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var response = context.Response;
        try
        {
            var request = context.Request;
            var path = request.Url?.AbsolutePath ?? "";
            var method = request.HttpMethod;
            var allowedOrigin = GetAllowedOrigin(request);

            // CORS preflight: respond before auth so browsers can complete the
            // handshake. The actual request that follows still goes through auth.
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
                // Include CORS headers so browser clients from allowed loopback
                // origins can actually read the 401 body and react.
                await WriteJsonAsync(
                    response,
                    401,
                    Serialize(new { error = "Unauthorized" }),
                    ct,
                    allowedOrigin
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
                    ct,
                    null
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
                _ => (404, Serialize(new { error = "Not found" }))
            };

            await WriteJsonAsync(response, statusCode, body, ct, allowedOrigin);
        }
        catch (HttpApiRequestException ex)
        {
            await WriteJsonAsync(
                response,
                ex.StatusCode,
                Serialize(new { error = ex.Message }),
                ct,
                GetAllowedOrigin(context.Request)
            );
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[HttpApiService] Request failed: {ex}");
            // Re-resolve origin from context.Request; allowedOrigin from the try
            // block isn't in scope here (the exception may have been thrown
            // before or after it was computed).
            var recoveredOrigin = GetAllowedOrigin(context.Request);
            await WriteJsonAsync(
                response,
                500,
                Serialize(new { error = "Internal server error" }),
                ct,
                recoveredOrigin
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
                    acceleration = BuildAccelerationDto(plugin, _settings.Current)
                }
            )
        );
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
            requiresRestart = status.RequiresRestart
        };
    }

    private static string FormatAccelerationBackend(TranscriptionAccelerationBackend backend) =>
        backend switch
        {
            TranscriptionAccelerationBackend.NvidiaCuda => "nvidia-cuda",
            _ => "cpu",
        };

    private (int, string) HandleModels()
    {
        var models = _models.PluginManager.TranscriptionEngines.SelectMany(engine =>
            engine.TranscriptionModels.Select(model =>
            {
                var id = ModelManagerService.GetPluginModelId(engine.PluginId, model.Id);
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
                        : "not_configured"
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
        // ContentLength64 is -1 for chunked (Transfer-Encoding: chunked) uploads.
        // Reject empty bodies and known-too-large bodies up front; let chunked
        // requests through so LimitedReadStream can enforce the cap while reading.
        if (request.ContentLength64 == 0 || request.ContentLength64 > MaxTranscribeRequestBytes)
        {
            return (413, Serialize(new { error = "Request body too large" }));
        }

        var apiRequest = await HttpApiRequestParser.FromListenerRequestAsync(
            request,
            MaxTranscribeRequestBytes,
            ct
        );
        var transcribeRequest = HttpApiRequestParser.ParseTranscribe(apiRequest);

        var tempPath = Path.Combine(
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
            return await RunTranscriptionAsync(tempPath, opts, ct);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch { }
        }
    }

    private async Task<(int, string)> HandleTranscribeLocalFileAsync(
        HttpListenerRequest request,
        CancellationToken ct
    )
    {
        var apiRequest = await HttpApiRequestParser.FromListenerRequestAsync(
            request,
            MaxTranscribeRequestBytes,
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
                apiRequest.Body,
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
            payload.LanguageHints ?? Array.Empty<string>(),
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

        // Refuse to silently block on a model download/restore when the caller
        // didn't opt in via await_download=1. EnsureModelLoadedCoreAsync would
        // otherwise call DownloadAndLoadModelCoreAsync for any missing local
        // model, hitting the CLI's 5-min budget before returning.
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
                        new
                        {
                            error = "Model is not downloaded. Pass await_download=1 to wait for the download."
                        }
                    )
                );
            }
        }

        // Decode audio before acquiring the lease — ffmpeg shells out and
        // must not monopolize the global model lock while no transcription
        // runs.
        var wav = await _audioFiles.LoadAudioAsWavAsync(audioPath, ct);
        var settings = _settings.Current;
        var language = opts.Language ?? (settings.Language == "auto" ? null : settings.Language);
        var prompt = MergePrompt(
            opts.Prompt,
            BuildLanguageHintsPrompt(opts.LanguageHints),
            _dictionary.GetTermsForPrompt()
        );

        // Hold the transcription lease only around plugin.TranscribeAsync so
        // a concurrent caller cannot swap the shared plugin's model mid-run.
        PluginTranscriptionResult result;
        string engineProviderId;
        string? selectedModelId;

        ModelManagerService.TranscriptionLease lease;
        try
        {
            lease = await _models.AcquireTranscriptionAsync(modelId, ct);
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
                DictionaryCorrector = _dictionary.ApplyCorrections
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
                    ct
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
                            text = segment.Text,
                            start = segment.Start,
                            end = segment.End
                        })
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
                    model = selectedModelId
                }
            )
        );
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
                words = record.WordCount
            });

        return (
            200,
            Serialize(
                new
                {
                    total = records.Count,
                    offset,
                    limit,
                    records = paged
                }
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
            promptActionId = profile.PromptActionId
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

        var profile = _profiles.Profiles.FirstOrDefault(item => item.Id == id);
        if (profile is null)
        {
            return (404, Serialize(new { error = "Profile not found" }));
        }

        var isEnabled = !profile.IsEnabled;
        _profiles.UpdateProfile(profile with { IsEnabled = isEnabled });
        return (200, Serialize(new { id, isEnabled }));
    }

    private async Task<(int, string)> HandleDictationStartAsync()
    {
        if (_dictation.IsRecording)
        {
            return (409, Serialize(new { error = "Already recording" }));
        }

        var sessionId = await _dictation.StartAsync();

        // The orchestrator can bail silently (no device, model load failure,
        // toggle gate already held); reflect actual state in the response.
        if (!_dictation.IsRecording)
        {
            return (409, Serialize(new { error = "Failed to start dictation" }));
        }

        // sessionId <= 0 means *this* call did not allocate the session — most
        // commonly because a concurrent /v1/dictation/start (or hotkey toggle)
        // already owned the toggle gate. The dictation that IS recording
        // belongs to that other caller and has a session id we can't surface
        // here, so we'd otherwise hand back `sessionId: 0` and the polling
        // client would never resolve. Treat it as a 409 instead.
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
                        message = stored.Message
                    }
                )
            );
        }

        if (_dictation.IsSessionInFlight(sessionId))
        {
            return (200, Serialize(new { state = "in_progress" }));
        }

        return (200, Serialize(new { state = "not_found" }));
    }

    private async Task<(int, string)> HandleDictationStopAsync()
    {
        if (!_dictation.IsRecording)
        {
            return (409, Serialize(new { error = "Not recording" }));
        }

        await _dictation.StopAsync();

        if (_dictation.IsRecording)
        {
            return (409, Serialize(new { error = "Failed to stop dictation" }));
        }

        return (200, Serialize(new { stopped = true }));
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
                    activeModel = _models.ActiveModelId
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
            MaxTranscribeRequestBytes,
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
                apiRequest.Body,
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
            MaxTranscribeRequestBytes,
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
                apiRequest.Body,
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
                        original = c.Original,
                        replacement = c.Replacement,
                        caseSensitive = c.CaseSensitive
                    }),
                    count = corrections.Count
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
            MaxTranscribeRequestBytes,
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
                apiRequest.Body,
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
                        original = c.Original,
                        replacement = c.Replacement,
                        caseSensitive = c.CaseSensitive
                    }),
                    count = corrections.Count
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
            MaxTranscribeRequestBytes,
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
                apiRequest.Body,
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
                        original = c.Original,
                        replacement = c.Replacement,
                        caseSensitive = c.CaseSensitive
                    }),
                    count = corrections.Count
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
                ? engine.SelectedModelId ?? engine.TranscriptionModels.FirstOrDefault()?.Id
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

            return ModelManagerService.GetPluginModelId(engine.PluginId, model);
        }

        if (!string.IsNullOrWhiteSpace(requestedModel))
        {
            var engine = _models.PluginManager.TranscriptionEngines.FirstOrDefault(candidate =>
                candidate.TranscriptionModels.Any(model => model.Id == requestedModel)
            );
            if (engine is null)
            {
                throw new HttpApiRequestException(404, $"Unknown model: {requestedModel}");
            }

            return ModelManagerService.GetPluginModelId(engine.PluginId, requestedModel);
        }

        return _settings.Current.SelectedModelId;
    }

    private static async Task WriteJsonAsync(
        HttpListenerResponse response,
        int statusCode,
        string body,
        CancellationToken ct,
        string? origin
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
            // Decrypt succeeded, so a token already exists. storedToken
            // equals decryptedToken only when it was stored as plaintext
            // by an older build (ApiKeyProtection.Decrypt is a no-op on
            // non-base64 blobs). Re-encrypt on the way through so the
            // stored value is always at-rest protected going forward.
            if (!string.Equals(storedToken, decryptedToken, StringComparison.Ordinal))
            {
                return;
            }

            _settings.Save(
                current with
                {
                    ApiServerBearerToken = ApiKeyProtection.Encrypt(decryptedToken)
                }
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
        // Short-circuit on length before FixedTimeEquals to avoid allocating
        // byte arrays of wildly different sizes, while keeping the constant-time
        // comparison for same-length inputs to prevent timing side-channels.
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

        // Only echo back origins on the same loopback address and same port
        // as the listener — prevents a cross-origin page from using the API
        // by claiming a localhost origin.
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
        if (string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        return string.Equals(origin, GetAllowedOrigin(request), StringComparison.OrdinalIgnoreCase);
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
}

internal sealed record DictionaryTermsRequest(IReadOnlyList<string> Terms, bool? Replace);