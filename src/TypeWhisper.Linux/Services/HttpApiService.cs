using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Ipc;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

internal sealed class HttpApiRequestDispatcher : IDisposable
{
    private static readonly TimeSpan s_drainTimeout = TimeSpan.FromSeconds(1);

    private readonly int _capacity;
    private readonly Action<Exception> _reportException;
    private readonly SemaphoreSlim _slots;

    public HttpApiRequestDispatcher(int capacity, Action<Exception>? reportException = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _slots = new SemaphoreSlim(capacity, capacity);
        _reportException = reportException ?? (ex =>
            Trace.WriteLine($"[HttpApiService] Dispatched request failed: {ex}"));
    }

    public Task? TryRun(Func<Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return _slots.Wait(0) ? RunAsync(handler) : null;
    }

    /// <summary>
    ///     Reclaims every slot first, proving no admitted handler is still in flight: a handler
    ///     releases its slot in a finally block, so disposing underneath one would surface an
    ///     <see cref="ObjectDisposedException" /> as an unobserved fault. A handler that outlasts
    ///     the drain leaves the semaphore undisposed, which is harmless — the Wait(0) path never
    ///     allocates a wait handle.
    /// </summary>
    public void Dispose()
    {
        for (var acquired = 0; acquired < _capacity; acquired++)
        {
            if (_slots.Wait(s_drainTimeout))
            {
                continue;
            }

            Trace.WriteLine(
                "[HttpApiService] Request slots still in use at dispose; leaving them undisposed."
            );
            return;
        }

        _slots.Dispose();
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

internal readonly record struct BearerTokenProtectionResult(
    string PlainText,
    string StoredValue,
    bool Changed
);

/// <summary>
///     Local HTTP API for dictation/transcription/history. Kestrel serves the
///     same API over loopback TCP and an owner-only Unix socket. CORS is echoed
///     only for the same loopback origin and port.
/// </summary>
public sealed partial class HttpApiService : IDisposable
{
    internal const int MaxConcurrentRequests = 2;
    internal const long MaxTranscribeRequestBytes = 100 * 1024 * 1024;

    // Applies to every JSON endpoint, including bulk PUT /v1/dictionary/terms uploads. A body
    // over this limit is rejected with 413 "Request body too large" rather than truncated, so
    // clients with a larger dictionary must split it across requests.
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
    private readonly HotkeyService _hotkeys;
    private readonly DictationOrchestrator _dictation;
    private readonly IDictionaryService _dictionary;
    private readonly ApiDiscoveryFile _discoveryFile;
    private readonly IHistoryService _history;
    private readonly ModelManagerService _models;
    private readonly IPostProcessingPipeline _pipeline;
    private readonly IProfileService _profiles;
    private readonly IPromptActionService _promptActions;
    private readonly HttpApiRequestDispatcher _requestDispatcher = new(MaxConcurrentRequests);
    private readonly DictationSessionResultStore _sessionResults;
    private readonly ISettingsService _settings;
    private readonly ITranslationService _translation;
    private readonly IVocabularyBoostingService _vocabularyBoosting;
    private readonly string? _apiSocketPathOverride;
    private readonly Func<Socket, bool> _validateUnixPeer;
    private readonly string _secretProtectionKeyFilePath;
    private bool _disposed;

    private WebApplication? _host;
    private ApiSocketOwnership? _ownership;
    private int _port;
    private string? _socketPath;

    public HttpApiService(
        ModelManagerService models,
        ISettingsService settings,
        AudioFileService audioFiles,
        IHistoryService history,
        IProfileService profiles,
        IPromptActionService promptActions,
        HotkeyService hotkeys,
        IDictionaryService dictionary,
        IVocabularyBoostingService vocabularyBoosting,
        IPostProcessingPipeline pipeline,
        ITranslationService translation,
        DictationOrchestrator dictation,
        DictationSessionResultStore sessionResults,
        ApiDiscoveryFile discoveryFile,
        string? secretProtectionKeyFilePath = null
    )
        : this(
            models,
            settings,
            audioFiles,
            history,
            profiles,
            promptActions,
            hotkeys,
            dictionary,
            vocabularyBoosting,
            pipeline,
            translation,
            dictation,
            sessionResults,
            discoveryFile,
            secretProtectionKeyFilePath,
            null,
            null
        )
    {
    }

    internal HttpApiService(
        ModelManagerService models,
        ISettingsService settings,
        AudioFileService audioFiles,
        IHistoryService history,
        IProfileService profiles,
        IPromptActionService promptActions,
        HotkeyService hotkeys,
        IDictionaryService dictionary,
        IVocabularyBoostingService vocabularyBoosting,
        IPostProcessingPipeline pipeline,
        ITranslationService translation,
        DictationOrchestrator dictation,
        DictationSessionResultStore sessionResults,
        ApiDiscoveryFile discoveryFile,
        string? secretProtectionKeyFilePath,
        string? apiSocketPath,
        Func<Socket, bool>? validateUnixPeer
    )
    {
        _models = models;
        _settings = settings;
        _audioFiles = audioFiles;
        _history = history;
        _profiles = profiles;
        _promptActions = promptActions;
        _hotkeys = hotkeys;
        _dictionary = dictionary;
        _vocabularyBoosting = vocabularyBoosting;
        _pipeline = pipeline;
        _translation = translation;
        _dictation = dictation;
        _sessionResults = sessionResults;
        _discoveryFile = discoveryFile;
        _apiSocketPathOverride = apiSocketPath;
        _validateUnixPeer =
            validateUnixPeer ?? UnixPeerCredentials.IsOwnedByEffectiveUser;
        _secretProtectionKeyFilePath =
            secretProtectionKeyFilePath
            ?? TypeWhisperEnvironment.SecretProtectionKeyFilePath;
    }

    public string StatusText { get; private set; } = "Local API is disabled.";

    private bool IsRunning => _host is not null;

    internal IHostLifetime? HostLifetime => _host?.Services.GetService<IHostLifetime>();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        // Stop() only tears down the host; admitted handlers run detached, so the dispatcher
        // does its own bounded drain before releasing the semaphore.
        _requestDispatcher.Dispose();
        _disposed = true;
    }

    private void Start(int port)
    {
        if (IsRunning && _port == port)
        {
            SetStatus(BuildRunningStatus(port, _socketPath, PublishDiscovery()));
            return;
        }

        if (port is <= 0 or > 65535)
        {
            Stop(false);
            SetStatus($"Local API failed to start: port must be 1–65535 (got {port}).");
            return;
        }

        Stop(false);

        ApiSocketOwnership? ownership = null;
        WebApplication? host = null;
        string? socketPath = null;
        try
        {
            socketPath = _apiSocketPathOverride ?? SocketPathResolver.ResolveApiSocketPath();
            if (!ApiSocketOwnership.TryAcquire(socketPath, out ownership))
            {
                throw new IOException($"API socket ownership is already held for {socketPath}.");
            }

            var cleanup = ownership.CleanupStaleSocket();
            if (cleanup is not (ApiSocketCleanupResult.Missing or ApiSocketCleanupResult.Removed))
            {
                throw new IOException(
                    $"API socket path {socketPath} could not be prepared ({cleanup})."
                );
            }

            host = BuildHost(port, socketPath);
            host.StartAsync().GetAwaiter().GetResult();
            SetOwnerOnlySocketMode(socketPath);

            _port = port;
            _socketPath = socketPath;
            _host = host;
            _ownership = ownership;
            host = null;
            ownership = null;

            SetStatus(BuildRunningStatus(port, socketPath, PublishDiscovery()));
        }
        catch (Exception ex)
        {
            StopHost(host);
            TryUnlinkSocket(socketPath, ownership);
            ownership?.Dispose();
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
            try
            {
                EnsureBearerToken();
            }
            catch (Exception ex) when (
                ex is CryptographicException
                    or IOException
                    or UnauthorizedAccessException
            )
            {
                Trace.WriteLine(
                    $"[HttpApiService] Bearer token protection unavailable: {ex.Message}"
                );
                Stop();
                SetStatus(Loc.Instance["Security.SecretProtectionUnavailable"]);
                return;
            }

            Start(_settings.Current.ApiServerPort);
        }
        else
        {
            Stop();
        }
    }

    internal static string ReadBearerToken(
        AppSettings settings,
        string? secretProtectionKeyFilePath = null
    )
    {
        if (string.IsNullOrWhiteSpace(settings.ApiServerBearerToken))
        {
            return "";
        }

        var result = ApiKeyProtection.Decrypt(
            settings.ApiServerBearerToken,
            secretProtectionKeyFilePath
        );
        return result.Succeeded ? result.PlainText ?? "" : "";
    }

    internal static object? BuildAccelerationDto(
        ITranscriptionEngineRole? plugin,
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
        var host = _host;
        var ownership = _ownership;
        var socketPath = _socketPath;
        _host = null;
        _ownership = null;
        _socketPath = null;
        _port = 0;
        _discoveryFile.Delete();
        StopHost(host);
        TryUnlinkSocket(socketPath, ownership);
        ownership?.Dispose();
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

    private bool PublishDiscovery()
    {
        var token = ReadBearerToken(
            _settings.Current,
            _secretProtectionKeyFilePath
        );
        return !string.IsNullOrWhiteSpace(token)
               && _socketPath is not null
               && _discoveryFile.Write(_port, token, _socketPath);
    }

    // The CLI reaches the API only through the discovery file's socket path, so a
    // failed publish is a client-visible outage even though the listeners are up.
    private static string BuildRunningStatus(int port, string? socketPath, bool discoveryPublished)
    {
        var running = $"Local API is running at http://localhost:{port}/ and {socketPath}.";
        return discoveryPublished
            ? running
            : $"{running} Discovery file could not be written — the CLI cannot connect.";
    }

    private static void SetOwnerOnlySocketMode(string socketPath)
    {
        const UnixFileMode ownerReadWrite =
            UnixFileMode.UserRead | UnixFileMode.UserWrite;
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
        File.SetUnixFileMode(socketPath, ownerReadWrite);
        if (File.GetUnixFileMode(socketPath) != ownerReadWrite)
#pragma warning restore CA1416
        {
            throw new IOException($"Could not secure API socket {socketPath} with mode 0600.");
        }
    }

    private static void StopHost(WebApplication? host)
    {
        if (host is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            host.StopAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[HttpApiService] Kestrel shutdown failed: {ex.Message}");
        }
        finally
        {
            try
            {
                host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[HttpApiService] Kestrel disposal failed: {ex.Message}");
            }
        }
    }

    private static void TryUnlinkSocket(
        string? socketPath,
        ApiSocketOwnership? ownership
    )
    {
        if (socketPath is null || ownership is null)
        {
            return;
        }

        try
        {
            var cleanup = ownership.CleanupStaleSocket();
            // ReSharper disable once ConvertIfStatementToSwitchStatement -- only the two "leave it alone" outcomes are reported; a switch would need its own missing-enum-cases suppression.
            if (cleanup is ApiSocketCleanupResult.Live)
            {
                Trace.WriteLine(
                    $"[HttpApiService] API socket path {socketPath} is held by a live listener; leaving it in place."
                );
            }
            else if (cleanup is ApiSocketCleanupResult.Indeterminate)
            {
                Trace.WriteLine(
                    $"[HttpApiService] API socket cleanup was indeterminate for {socketPath}; leaving it in place."
                );
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[HttpApiService] Could not remove API socket {socketPath}: {ex.Message}"
            );
        }
    }

    private WebApplication BuildHost(int port, string socketPath)
    {
        var builder = WebApplication.CreateSlimBuilder(
            new WebApplicationOptions
            {
                Args = [],
                ApplicationName = typeof(HttpApiService).Assembly.GetName().Name,
            }
        );
        // Drop appsettings.json / environment / command-line sources: Kestrel *adds*
        // an ambient Kestrel:Endpoints entry to the listeners configured below rather
        // than replacing them, which would bind this local-only API to a public interface.
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        // ConsoleLifetime would install SIGINT/SIGQUIT/SIGTERM handlers that cancel
        // the signal and only stop this embedded host, leaving the desktop app alive
        // and unkillable while the API is enabled.
        builder.Services.AddSingleton<IHostLifetime, EmbeddedHostLifetime>();
        builder.WebHost.ConfigureKestrel(options =>
        {
            // The request parser's 100 MiB / 1 MiB route-specific limits remain
            // authoritative instead of Kestrel's lower 30 MB default.
            options.Limits.MaxRequestBodySize = null;
            options.ListenLocalhost(port);
            options.ListenUnixSocket(socketPath, listenOptions =>
            {
                // This boundary runs before HTTP parses headers or bodies. Rejecting
                // here prevents a different UID from presenting bearer data or audio.
                listenOptions.Use(next => async connection =>
                {
                    var socket = connection.Features.Get<IConnectionSocketFeature>()?.Socket;
                    bool owned;
                    try
                    {
                        // A credential read that fails tells us nothing about the peer,
                        // so it must fail closed here rather than unwind into Kestrel.
                        owned = socket is not null && _validateUnixPeer(socket);
                    }
                    catch (IOException)
                    {
                        owned = false;
                    }

                    if (!owned)
                    {
                        connection.Abort();
                        return;
                    }

                    await next(connection);
                });
            });
        });

        var app = builder.Build();
        app.Run(DispatchRequestAsync);
        return app;
    }

    private async Task DispatchRequestAsync(HttpContext context)
    {
        var handlerTask = _requestDispatcher.TryRun(() =>
            HandleRequestAsync(context, context.RequestAborted)
        );
        if (handlerTask is null)
        {
            await RejectOverCapacityAsync(context, context.RequestAborted);
            return;
        }

        await handlerTask;
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
        HttpContext context,
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
            await response.CompleteAsync();
        }
    }

    private async Task HandleRequestAsync(HttpContext context, CancellationToken ct)
    {
        var response = context.Response;
        try
        {
            var request = context.Request;
            var path = request.Path.Value ?? "";
            var method = request.Method;
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
                response.ContentLength = 0;
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

            if (!IsValidOrigin(request) || !IsAllowedLoopbackHost(request.Host.Host))
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
                ("/v1/transcribe", "POST") => await HandleTranscribeAsync(context, ct),
                ("/v1/transcribe/local-file", "POST") =>
                    await HandleTranscribeLocalFileAsync(context, ct),
                ("/v1/history", "GET") => HandleHistorySearch(request),
                ("/v1/history", "DELETE") => HandleHistoryDelete(request),
                ("/v1/profiles", "GET") => HandleProfilesList(),
                ("/v1/profiles/toggle", "PUT") => HandleProfileToggle(request),
                ("/v1/dictation/start", "POST") => await HandleDictationStartAsync(),
                ("/v1/dictation/stop", "POST") => await HandleDictationStopAsync(),
                ("/v1/dictation/status", "GET") => HandleDictationStatus(),
                ("/v1/dictation/transcription", "GET") => HandleDictationTranscription(request),
                ("/v1/dictionary/terms", "GET") => HandleGetDictionaryTerms(),
                ("/v1/dictionary/terms", "PUT") => await HandlePutDictionaryTermsAsync(context, ct),
                ("/v1/dictionary/terms", "DELETE") =>
                    await HandleDeleteDictionaryTermAsync(context, ct),
                ("/v1/dictionary/corrections", "GET") => HandleGetDictionaryCorrections(),
                ("/v1/dictionary/corrections", "PUT") =>
                    await HandlePutDictionaryCorrectionAsync(context, ct),
                ("/v1/dictionary/corrections", "DELETE") =>
                    await HandleDeleteDictionaryCorrectionAsync(context, ct),
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
        catch (InvalidLanguageSelectionException ex)
        {
            await WriteJsonAsync(
                response,
                400,
                Serialize(
                    new
                    {
                        error = ex.Message,
                        reason = "invalid_language_selection",
                    }
                ),
                GetAllowedOrigin(context.Request),
                ct
            );
        }
        catch (LanguageSelectionNotSupportedException ex)
        {
            await WriteJsonAsync(
                response,
                400,
                Serialize(
                    new
                    {
                        error = ex.Message,
                        reason = "language_selection_not_supported",
                    }
                ),
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
            await response.CompleteAsync();
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
        HttpContext context,
        CancellationToken ct
    )
    {
        // Empty body — answer with the same contract ParseTranscribe would produce.
        if (context.Request.ContentLength == 0)
        {
            return (400, Serialize(new { error = "No audio data provided" }));
        }

        var prepared = await PrepareTranscriptionRequestAsync(context, ct);
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
        HttpContext context,
        CancellationToken ct
    )
    {
        var apiRequest = await HttpApiRequestParser.FromHttpContextAsync(
            context,
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
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            if (!OperatingSystem.IsWindows())
            {
                fileOptions.UnixCreateMode =
                    UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var destination = new FileStream(tempPath, fileOptions))
            {
                await destination.WriteAsync(transcribeRequest.AudioData, ct);
            }
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
        HttpContext context,
        CancellationToken ct
    )
    {
        var apiRequest = await HttpApiRequestParser.FromHttpContextAsync(
            context,
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

        var (task, responseFormat) = HttpApiRequestParser.ParseTranscriptionOptions(
            payload.Task,
            payload.ResponseFormat
        );
        var opts = new TranscriptionRunOptions(
            payload.Language,
            // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract -- LanguageHints is deserialized from JSON and can be null when the field is omitted
            payload.LanguageHints ?? [],
            task,
            payload.TargetLanguage,
            responseFormat,
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

        var settings = _settings.Current;
        var languageSelection = LanguageSelectionResolver.Resolve(
            opts.Language,
            settings.Language
        );
        var configuredLanguage = languageSelection.LanguageTag;

        // Decode audio before acquiring the lease — ffmpeg shells out and
        // must not hold the model lock while no transcription runs.
        var wav = await _audioFiles.LoadAudioAsWavAsync(audioPath, ct);
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
        bool engineSupportsTranslation;

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
                languageSelection,
                opts.Task == TranscriptionTask.Translate,
                prompt,
                ct
            );
            engineProviderId = plugin.ProviderId;
            selectedModelId = plugin.SelectedModelId;
            engineSupportsTranslation = plugin.SupportsTranslation;
        }

        // An engine that ignores the translate task returns source-language text; reporting
        // Translate downstream would make number normalization treat it as English.
        var effectiveTask =
            opts.Task == TranscriptionTask.Translate && engineSupportsTranslation
                ? TranscriptionTask.Translate
                : TranscriptionTask.Transcribe;

        var processed = await _pipeline.ProcessAsync(
            result.Text,
            new PipelineOptions
            {
                VocabularyBooster = settings.VocabularyBoostingEnabled
                    ? _vocabularyBoosting.Apply
                    : null,
                DictionaryCorrector = _dictionary.ApplyCorrections,
                TranscriptionTask = effectiveTask,
                DetectedLanguage = result.DetectedLanguage,
                ConfiguredLanguage = configuredLanguage,
                ConfiguredLanguageCandidates = opts.LanguageHints,
                TranscriptionNumberNormalizationEnabled =
                    settings.TranscriptionNumberNormalizationEnabled,
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
                    result.DetectedLanguage ?? configuredLanguage ?? "en",
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

    private (int, string) HandleHistorySearch(HttpRequest request)
    {
        var query = request.Query["q"].ToString();
        var limit = int.TryParse(request.Query["limit"].ToString(), out var parsedLimit)
            ? parsedLimit
            : 50;
        var offset = int.TryParse(request.Query["offset"].ToString(), out var parsedOffset)
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

    private (int, string) HandleHistoryDelete(HttpRequest request)
    {
        var id = request.Query["id"].ToString();
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

    private (int, string) HandleProfileToggle(HttpRequest request)
    {
        var id = request.Query["id"].ToString();
        if (string.IsNullOrWhiteSpace(id))
        {
            return (400, Serialize(new { error = "Missing id parameter" }));
        }

        var profiles = _profiles.Profiles;
        var current = profiles.FirstOrDefault(profile => profile.Id == id);
        if (current is null)
        {
            return (404, Serialize(new { error = "Profile not found" }));
        }

        if (!current.IsEnabled)
        {
            var hotkeyValidation = _hotkeys.ValidateProfileHotkeyCandidate(
                current.HotkeyData,
                current.HotkeyBehavior,
                current.PromptActionId,
                current.Id,
                _promptActions.Actions,
                profiles
            );
            if (!hotkeyValidation.IsValid)
            {
                var (reason, error) = GetProfileToggleValidationFailure(
                    hotkeyValidation.Status
                );
                return (409, Serialize(new { error, reason }));
            }
        }

        // Snapshot-then-toggle: a concurrent enable can slip through; dynamic
        // reconciliation rejects the loser and logs it.
        var profile = _profiles.ToggleProfileEnabled(id);
        if (profile is null)
        {
            return (404, Serialize(new { error = "Profile not found" }));
        }

        var isEnabled = profile.IsEnabled;
        return (200, Serialize(new { id, isEnabled }));
    }

    private static (string Reason, string Error) GetProfileToggleValidationFailure(
        HotkeyCandidateValidationStatus status
    )
    {
        return status switch
        {
            HotkeyCandidateValidationStatus.Malformed =>
                ("hotkey-malformed", "Profile hotkey cannot be enabled because it is malformed."),
            HotkeyCandidateValidationStatus.MissingEnabledPromptAction =>
                (
                    "prompt-action-required",
                    "Profile hotkey cannot be enabled because its selected-text prompt action is missing or disabled."
                ),
            _ =>
                (
                    "hotkey-collision",
                    "Profile hotkey cannot be enabled because it conflicts with an enabled shortcut."
                ),
        };
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

    private (int, string) HandleDictationTranscription(HttpRequest request)
    {
        var sessionIdRaw = request.Query["sessionId"].ToString();
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
        HttpContext context,
        CancellationToken ct
    )
    {
        var apiRequest = await HttpApiRequestParser.FromHttpContextAsync(
            context,
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
        HttpContext context,
        CancellationToken ct
    )
    {
        var apiRequest = await HttpApiRequestParser.FromHttpContextAsync(
            context,
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
        HttpContext context,
        CancellationToken ct
    )
    {
        var apiRequest = await HttpApiRequestParser.FromHttpContextAsync(
            context,
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
        HttpContext context,
        CancellationToken ct
    )
    {
        var apiRequest = await HttpApiRequestParser.FromHttpContextAsync(
            context,
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
        HttpResponse response,
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
        response.ContentLength = bytes.Length;
        await response.Body.WriteAsync(bytes, ct);
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
        var protectedToken = ProtectBearerToken(
            current.ApiServerBearerToken,
            _secretProtectionKeyFilePath
        );
        if (protectedToken.Changed)
        {
            _settings.Save(
                current with { ApiServerBearerToken = protectedToken.StoredValue }
            );
        }
    }

    internal static BearerTokenProtectionResult ProtectBearerToken(
        string? storedValue,
        string? secretProtectionKeyFilePath = null
    )
    {
        if (!string.IsNullOrWhiteSpace(storedValue))
        {
            var decrypted = ApiKeyProtection.Decrypt(
                storedValue,
                secretProtectionKeyFilePath
            );
            if (
                decrypted.Succeeded
                && !string.IsNullOrWhiteSpace(decrypted.PlainText)
            )
            {
                if (decrypted.Format == SecretProtectionFormat.Current)
                {
                    return new BearerTokenProtectionResult(
                        decrypted.PlainText,
                        storedValue,
                        false
                    );
                }

                return new BearerTokenProtectionResult(
                    decrypted.PlainText,
                    ApiKeyProtection.Encrypt(
                        decrypted.PlainText,
                        secretProtectionKeyFilePath
                    ),
                    true
                );
            }

            // Pre-encryption builds stored the generated token as plaintext hex. That decodes to
            // a CBC-shaped envelope that cannot be authenticated, and rotating it would break
            // external clients already holding the token, so re-protect it instead.
            if (LegacyPlaintextTokenRegex().IsMatch(storedValue))
            {
                return new BearerTokenProtectionResult(
                    storedValue,
                    ApiKeyProtection.Encrypt(storedValue, secretProtectionKeyFilePath),
                    true
                );
            }
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return new BearerTokenProtectionResult(
            token,
            ApiKeyProtection.Encrypt(token, secretProtectionKeyFilePath),
            true
        );
    }

    private bool IsAuthorized(HttpRequest request)
    {
        var expectedToken = ReadBearerToken(
            _settings.Current,
            _secretProtectionKeyFilePath
        );
        if (string.IsNullOrWhiteSpace(expectedToken))
        {
            return false;
        }

        var authorization = request.Headers.Authorization.ToString();
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

    private string? GetAllowedOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
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

    private bool IsValidOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
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

    [GeneratedRegex("^[0-9A-Fa-f]{64}$")]
    private static partial Regex LegacyPlaintextTokenRegex();

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

    /// <summary>Host lifetime that owns no process signals — the desktop app owns shutdown.</summary>
    private sealed class EmbeddedHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}

internal sealed record DictionaryTermsRequest(IReadOnlyList<string> Terms, bool? Replace);
