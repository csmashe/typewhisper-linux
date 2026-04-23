using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

public sealed class HttpApiService : IDisposable
{
    private const long MaxTranscribeRequestBytes = 100 * 1024 * 1024;
    private readonly ModelManagerService _models;
    private readonly ISettingsService _settings;
    private readonly AudioFileService _audioFiles;
    private readonly IHistoryService _history;
    private readonly IProfileService _profiles;
    private readonly IDictionaryService _dictionary;
    private readonly IVocabularyBoostingService _vocabularyBoosting;
    private readonly IPostProcessingPipeline _pipeline;
    private readonly DictationOrchestrator _dictation;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private int _port;
    private bool _disposed;

    public event Action? StateChanged;

    public string StatusText { get; private set; } = "Local API is disabled.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    public bool IsRunning => _listener?.IsListening == true;

    public HttpApiService(
        ModelManagerService models,
        ISettingsService settings,
        AudioFileService audioFiles,
        IHistoryService history,
        IProfileService profiles,
        IDictionaryService dictionary,
        IVocabularyBoostingService vocabularyBoosting,
        IPostProcessingPipeline pipeline,
        DictationOrchestrator dictation)
    {
        _models = models;
        _settings = settings;
        _audioFiles = audioFiles;
        _history = history;
        _profiles = profiles;
        _dictionary = dictionary;
        _vocabularyBoosting = vocabularyBoosting;
        _pipeline = pipeline;
        _dictation = dictation;
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
            Stop(updateStatus: false);
            SetStatus("Local API failed to start: port must be between 1 and 65535.");
            return;
        }

        Stop(updateStatus: false);

        try
        {
            _port = port;
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
            SetStatus($"Local API is running at http://localhost:{port}/");
        }
        catch (Exception ex)
        {
            Stop(updateStatus: false);
            SetStatus($"Local API failed to start: {ex.Message}");
        }
    }

    public void Stop() => Stop(updateStatus: true);

    private void Stop(bool updateStatus)
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        _listener = null;
        _port = 0;
        if (updateStatus)
            SetStatus("Local API is disabled.");
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
            Stop();
    }

    private void SetStatus(string status)
    {
        if (StatusText == status)
            return;

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
                    response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS";
                    response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
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
                await WriteJsonAsync(response, 401, Serialize(new { error = "Unauthorized" }), ct, allowedOrigin);
                return;
            }

            if (!IsValidOrigin(request) || !IsAllowedLoopbackHost(request.Url?.Host))
            {
                // Origin itself is forbidden — do not send CORS to it.
                await WriteJsonAsync(response, 403, Serialize(new { error = "Forbidden" }), ct, origin: null);
                return;
            }

            var (statusCode, body) = (path, method) switch
            {
                ("/v1/status", "GET") => HandleStatus(),
                ("/v1/models", "GET") => HandleModels(),
                ("/v1/transcribe", "POST") => await HandleTranscribeAsync(request, ct),
                ("/v1/history", "GET") => HandleHistorySearch(request),
                ("/v1/history", "DELETE") => HandleHistoryDelete(request),
                ("/v1/profiles", "GET") => HandleProfilesList(),
                ("/v1/profiles/toggle", "PUT") => HandleProfileToggle(request),
                ("/v1/dictation/start", "POST") => await HandleDictationStartAsync(),
                ("/v1/dictation/stop", "POST") => await HandleDictationStopAsync(),
                ("/v1/dictation/status", "GET") => HandleDictationStatus(),
                _ => (404, Serialize(new { error = "Not found" }))
            };

            await WriteJsonAsync(response, statusCode, body, ct, allowedOrigin);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[HttpApiService] Request failed: {ex}");
            // Re-resolve origin from context.Request; allowedOrigin from the try
            // block isn't in scope here (the exception may have been thrown
            // before or after it was computed).
            var recoveredOrigin = GetAllowedOrigin(context.Request);
            await WriteJsonAsync(response, 500, Serialize(new { error = "Internal server error" }), ct, recoveredOrigin);
        }
        finally
        {
            response.Close();
        }
    }

    private (int, string) HandleStatus()
    {
        var plugin = _models.ActiveTranscriptionPlugin;
        return (200, Serialize(new
        {
            status = _models.ActiveModelId is not null ? "ready" : "no_model",
            activeModel = _models.ActiveModelId,
            apiVersion = "1.0",
            supportsStreaming = plugin?.SupportsStreaming ?? false,
            supportsTranslation = plugin?.SupportsTranslation ?? false
        }));
    }

    private (int, string) HandleModels()
    {
        var models = _models.PluginManager.TranscriptionEngines
            .SelectMany(engine => engine.TranscriptionModels.Select(model =>
            {
                var id = ModelManagerService.GetPluginModelId(engine.PluginId, model.Id);
                return new
                {
                    id,
                    name = $"{engine.ProviderDisplayName}: {model.DisplayName}",
                    size = model.SizeDescription ?? (engine.SupportsModelDownload ? "Local" : "Cloud"),
                    engine = engine.PluginId,
                    downloaded = _models.IsDownloaded(id),
                    active = _models.ActiveModelId == id
                };
            }));

        return (200, Serialize(new { models }));
    }

    private async Task<(int, string)> HandleTranscribeAsync(HttpListenerRequest request, CancellationToken ct)
    {
        // ContentLength64 is -1 for chunked (Transfer-Encoding: chunked) uploads.
        // Reject empty bodies and known-too-large bodies up front; let chunked
        // requests through so LimitedReadStream can enforce the cap while reading.
        if (request.ContentLength64 == 0 || request.ContentLength64 > MaxTranscribeRequestBytes)
            return (413, Serialize(new { error = "Request body too large" }));

        var modelId = request.QueryString["model"] ?? _settings.Current.SelectedModelId;
        if (!await _models.EnsureModelLoadedAsync(modelId, ct))
            return (503, Serialize(new { error = "No model loaded" }));

        var plugin = _models.ActiveTranscriptionPlugin;
        if (plugin is null)
            return (503, Serialize(new { error = "No transcription engine loaded" }));

        var tempPath = Path.Combine(Path.GetTempPath(), $"typewhisper-api-{Guid.NewGuid():N}{InferExtension(request)}");
        try
        {
            try
            {
                await using var fs = File.Create(tempPath);
                await using var limited = new LimitedReadStream(request.InputStream, MaxTranscribeRequestBytes);
                await limited.CopyToAsync(fs, ct);
            }
            catch (InvalidOperationException)
            {
                return (413, Serialize(new { error = "Request body too large" }));
            }

            var wav = await _audioFiles.LoadAudioAsWavAsync(tempPath, ct);
            var settings = _settings.Current;
            var language = request.QueryString["language"] ?? (settings.Language == "auto" ? null : settings.Language);
            var translate = string.Equals(
                request.QueryString["task"] ?? settings.TranscriptionTask,
                "translate",
                StringComparison.OrdinalIgnoreCase);

            var result = await plugin.TranscribeAsync(wav, language, translate, null, ct);
            var processed = await _pipeline.ProcessAsync(result.Text, new PipelineOptions
            {
                VocabularyBooster = settings.VocabularyBoostingEnabled ? _vocabularyBoosting.Apply : null,
                DictionaryCorrector = _dictionary.ApplyCorrections
            }, ct);

            return (200, Serialize(new
            {
                text = processed.Text,
                language = result.DetectedLanguage,
                duration = result.DurationSeconds,
                noSpeechProbability = result.NoSpeechProbability
            }));
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private (int, string) HandleHistorySearch(HttpListenerRequest request)
    {
        var query = request.QueryString["q"] ?? "";
        var limit = int.TryParse(request.QueryString["limit"], out var parsedLimit) ? parsedLimit : 50;
        var offset = int.TryParse(request.QueryString["offset"], out var parsedOffset) ? parsedOffset : 0;

        var records = string.IsNullOrWhiteSpace(query)
            ? _history.Records
            : _history.Search(query);

        var paged = records.Skip(offset).Take(limit).Select(record => new
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

        return (200, Serialize(new
        {
            total = records.Count,
            offset,
            limit,
            records = paged
        }));
    }

    private (int, string) HandleHistoryDelete(HttpListenerRequest request)
    {
        var id = request.QueryString["id"];
        if (string.IsNullOrWhiteSpace(id))
            return (400, Serialize(new { error = "Missing id parameter" }));

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
            return (400, Serialize(new { error = "Missing id parameter" }));

        var profile = _profiles.Profiles.FirstOrDefault(item => item.Id == id);
        if (profile is null)
            return (404, Serialize(new { error = "Profile not found" }));

        var isEnabled = !profile.IsEnabled;
        _profiles.UpdateProfile(profile with { IsEnabled = isEnabled });
        return (200, Serialize(new { id, isEnabled }));
    }

    private async Task<(int, string)> HandleDictationStartAsync()
    {
        if (_dictation.IsRecording)
            return (409, Serialize(new { error = "Already recording" }));

        await _dictation.StartAsync();

        // The orchestrator can bail silently (no device, model load failure,
        // toggle gate already held); reflect actual state in the response.
        if (!_dictation.IsRecording)
            return (409, Serialize(new { error = "Failed to start dictation" }));

        return (200, Serialize(new { started = true }));
    }

    private async Task<(int, string)> HandleDictationStopAsync()
    {
        if (!_dictation.IsRecording)
            return (409, Serialize(new { error = "Not recording" }));

        await _dictation.StopAsync();

        if (_dictation.IsRecording)
            return (409, Serialize(new { error = "Failed to stop dictation" }));

        return (200, Serialize(new { stopped = true }));
    }

    private (int, string) HandleDictationStatus() =>
        (200, Serialize(new
        {
            state = _dictation.IsRecording ? "recording" : "idle",
            isRecording = _dictation.IsRecording,
            activeModel = _models.ActiveModelId
        }));

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, string body, CancellationToken ct, string? origin)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        if (!string.IsNullOrWhiteSpace(origin))
        {
            response.Headers["Access-Control-Allow-Origin"] = origin;
            response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
        }

        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static string InferExtension(HttpListenerRequest request)
    {
        var fileName = request.QueryString["filename"];
        var ext = string.IsNullOrWhiteSpace(fileName) ? null : Path.GetExtension(fileName);
        if (!string.IsNullOrWhiteSpace(ext))
            return ext;

        return request.ContentType?.Split(';', 2)[0].Trim().ToLowerInvariant() switch
        {
            "audio/mpeg" => ".mp3",
            "audio/mp3" => ".mp3",
            "audio/mp4" => ".m4a",
            "audio/aac" => ".aac",
            "audio/ogg" => ".ogg",
            "audio/flac" => ".flac",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            _ => ".wav"
        };
    }

    private void EnsureBearerToken()
    {
        var current = _settings.Current;
        var storedToken = current.ApiServerBearerToken;
        var decryptedToken = ReadBearerToken(current);
        if (!string.IsNullOrWhiteSpace(decryptedToken))
        {
            if (!string.Equals(storedToken, decryptedToken, StringComparison.Ordinal))
                return;

            _settings.Save(current with { ApiServerBearerToken = ApiKeyProtection.Encrypt(decryptedToken) });
            return;
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _settings.Save(current with { ApiServerBearerToken = ApiKeyProtection.Encrypt(token) });
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var expectedToken = ReadBearerToken(_settings.Current);
        if (string.IsNullOrWhiteSpace(expectedToken))
            return false;

        var authorization = request.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(authorization) || !authorization.StartsWith("Bearer ", StringComparison.Ordinal))
            return false;

        var providedToken = authorization["Bearer ".Length..].Trim();
        if (providedToken.Length != expectedToken.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(providedToken),
            Encoding.UTF8.GetBytes(expectedToken));
    }

    internal static string ReadBearerToken(Core.Models.AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.ApiServerBearerToken)
            ? ""
            : ApiKeyProtection.Decrypt(settings.ApiServerBearerToken);

    private string? GetAllowedOrigin(HttpListenerRequest request)
    {
        var origin = request.Headers["Origin"];
        if (string.IsNullOrWhiteSpace(origin))
            return null;

        if (Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            && IsAllowedLoopbackHost(originUri.Host)
            && originUri.Port == _port)
        {
            return origin;
        }

        return null;
    }

    private bool IsValidOrigin(HttpListenerRequest request)
    {
        var origin = request.Headers["Origin"];
        if (string.IsNullOrWhiteSpace(origin))
            return true;

        return string.Equals(origin, GetAllowedOrigin(request), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class LimitedReadStream(Stream inner, long maxBytes) : Stream
    {
        private readonly Stream _inner = inner;
        private readonly long _maxBytes = maxBytes;
        private long _bytesRead;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _bytesRead;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            TrackBytes(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            TrackBytes(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            TrackBytes(read);
            return read;
        }

        public override int ReadByte()
        {
            var value = _inner.ReadByte();
            if (value >= 0)
                TrackBytes(1);
            return value;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void TrackBytes(int read)
        {
            _bytesRead += read;
            if (_bytesRead > _maxBytes)
                throw new InvalidOperationException("Request body exceeded the configured limit.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _cts?.Dispose();
        try { _listenTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _disposed = true;
    }
}
