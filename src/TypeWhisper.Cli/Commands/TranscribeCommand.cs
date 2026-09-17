using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.Cli.Models;
using TypeWhisper.Cli.Output;
using TypeWhisper.Cli.Services;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Cli.Commands;

/// <summary>
///     Implements <c>typewhisper transcribe &lt;file|-&gt;</c>: passes a local audio
///     path (spooling stdin to a private file) to the API and prints the transcript
///     or JSON response.
/// </summary>
internal static partial class TranscribeCommand
{
    // Longest magic-byte window StdinAudioSniffer.Detect inspects (RIFF/WAVE).
    private const int SniffHeadBytes = 12;

    private const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static Task<int> RunAsync(ApiClient api, CliOptions options, CancellationToken ct = default)
    {
        return RunAsync(api, options, Console.OpenStandardInput(), ct: ct);
    }

    internal static async Task<int> RunAsync(
        ApiClient api,
        CliOptions options,
        Stream stdin,
        TimeSpan? budget = null,
        Func<bool>? isInputRedirected = null,
        CancellationToken ct = default
    )
    {
        if (options is { Json: true, IsRawResponse: true })
        {
            return ConsoleOutput.Error("--json cannot be combined with a non-JSON --response-format.");
        }

        if (!string.IsNullOrEmpty(options.Language) && options.LanguageHints.Count > 0)
        {
            return ConsoleOutput.Error("--language and --language-hint cannot be used together.");
        }

        string? canonicalLanguage = null;
        if (!string.IsNullOrWhiteSpace(options.Language))
        {
            if (!LanguageSelection.TryParse(options.Language, out var selection))
            {
                return ConsoleOutput.Error(
                    $"Invalid value '{options.Language.Trim()}' for --language. Use 'auto' or a valid BCP-47 tag."
                );
            }

            canonicalLanguage = selection.ToString();
        }

        var file = options.Positionals.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(file))
        {
            if (!(isInputRedirected?.Invoke() ?? Console.IsInputRedirected))
            {
                return ConsoleOutput.Error("Provide an audio file or pipe audio to stdin.");
            }

            file = "-";
        }

        var requestBudget = budget ?? (options.AwaitDownload
            ? TimeSpan.FromMinutes(15)
            : TimeSpan.FromMinutes(5));
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        requestCts.CancelAfter(requestBudget);
        string? spoolPath = null;
        try
        {
            requestCts.Token.ThrowIfCancellationRequested();
            string localPath;
            if (file == "-")
            {
                try
                {
                    spoolPath = await SpoolStdinAsync(stdin, requestCts.Token);
                    if (spoolPath is null)
                    {
                        // Multipart already 400s empty bodies; local-file has no such
                        // check and would spawn ffmpeg on nothing, returning a bare 500.
                        return ConsoleOutput.Error("Empty audio data on stdin.");
                    }

                    localPath = spoolPath;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException)
                {
                    return ConsoleOutput.Error($"Could not spool stdin: {ex.Message}");
                }
            }
            else
            {
                if (!File.Exists(file))
                {
                    return ConsoleOutput.Error($"File not found: {file}");
                }

                localPath = Path.GetFullPath(file);
            }

            // Multipart trimmed fields and dropped blanks server-side; local-file
            // forwards the body verbatim, so trim here to keep --engine " whisper " working.
            var request = new LocalFileTranscribeRequest(
                localPath,
                canonicalLanguage,
                [.. options.LanguageHints.Select(Clean).OfType<string>()],
                Clean(options.Task),
                Clean(options.TranslateTo),
                Clean(options.ResponseFormat),
                Clean(options.Prompt),
                Clean(options.Engine),
                Clean(options.Model),
                options.AwaitDownload,
                options.NoCorrections ? false : null
            );
            using var content = new StringContent(
                JsonSerializer.Serialize(
                    request,
                    TranscribeJsonContext.Default.LocalFileTranscribeRequest
                ),
                Encoding.UTF8,
                "application/json"
            );
            using var response = await api.TranscribeHttp.PostAsync(
                $"{api.BaseUrl}/v1/transcribe/local-file",
                content,
                requestCts.Token
            );
            var body = await response.Content.ReadAsStringAsync(requestCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return ConsoleOutput.Error(
                    $"Transcription failed ({(int)response.StatusCode}): {JsonFormatting.ExtractErrorMessage(body)}",
                    ExitCodes.ServerError
                );
            }

            if (options.IsRawResponse)
            {
                Console.Write(body);
                return ExitCodes.Success;
            }

            var validation = ApiResponseValidator.ValidateTranscribe(body);
            if (validation.Error is not null)
            {
                return ApiResponseValidator.ProtocolError(validation.Error);
            }

            if (options.Json)
            {
                Console.WriteLine(JsonFormatting.PrettyJson(body));
                return 0;
            }

            Console.WriteLine(validation.Value!.Text);
            return 0;
        }
        catch (HttpRequestException)
        {
            return ConsoleOutput.Error("TypeWhisper is not running or API server is disabled.", ExitCodes.Unavailable);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ConsoleOutput.Error("Cancelled.");
        }
        catch (OperationCanceledException)
        {
            return ConsoleOutput.Error(
                options.AwaitDownload
                    ? "Transcription timed out while waiting for model download."
                    : "Transcription timed out.",
                ExitCodes.Unavailable
            );
        }
        catch (JsonException)
        {
            return ApiResponseValidator.ProtocolError("transcription response body is not valid JSON");
        }
        finally
        {
            if (spoolPath is not null)
            {
                TryDeleteSpoolFile(spoolPath);
            }
        }
    }

    private static void TryDeleteSpoolFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup must never replace the command's result or the spool failure that
            // got us here — but the file holds the whole recording, so say where it is.
            Console.Error.WriteLine(
                $"Warning: could not remove the temporary audio file {path}: {ex.Message}"
            );
        }
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    ///     Spools stdin to a private temp file, returning <c>null</c> when stdin was
    ///     empty so the caller can report it without creating the file.
    /// </summary>
    private static async Task<string?> SpoolStdinAsync(Stream stdin, CancellationToken ct)
    {
        // A pipe may satisfy a read with fewer bytes than were asked for, so fill
        // the whole sniff window before detecting; otherwise a short first read
        // mis-detects the container as the "wav" default.
        var head = new byte[SniffHeadBytes];
        var headLength = 0;
        while (headLength < head.Length)
        {
            var headRead = await ReadStdinAsync(stdin, head.AsMemory(headLength), ct);
            if (headRead == 0)
            {
                break;
            }

            headLength += headRead;
        }

        // The head loop only stops short of the window at EOF, so no bytes here means
        // stdin was empty.
        if (headLength == 0)
        {
            return null;
        }

        var extension = StdinAudioSniffer.Detect(head.AsSpan(0, headLength));
        var spoolPath = Path.GetFullPath(
            Path.Join(
                Path.GetTempPath(),
                $"typewhisper-stdin-{Guid.NewGuid():N}.{extension}"
            )
        );

        try
        {
            var spoolOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            // Apply 0600 at open time so there is no chmod-after-create window.
            if (!OperatingSystem.IsWindows())
            {
                spoolOptions.UnixCreateMode = PrivateFileMode;
            }

            await using var spool = new FileStream(
                spoolPath,
                spoolOptions
            );
            await spool.WriteAsync(head.AsMemory(0, headLength), ct);

            // The local-file route has no audio-body limit, so stream until EOF and
            // let available temporary storage be the natural bound.
            var chunk = new byte[81920];
            int read;
            while ((read = await ReadStdinAsync(stdin, chunk, ct)) > 0)
            {
                await spool.WriteAsync(chunk.AsMemory(0, read), ct);
            }

            return spoolPath;
        }
        catch
        {
            TryDeleteSpoolFile(spoolPath);
            throw;
        }
    }

    // Console stdin only checks the token before its blocking read, so read on the
    // pool and abandon the read when cancelled or out of budget.
    private static Task<int> ReadStdinAsync(Stream stdin, Memory<byte> buffer, CancellationToken ct)
    {
        return Task.Run(() => stdin.ReadAsync(buffer, ct).AsTask(), ct).WaitAsync(ct);
    }

    // Properties are read by the source-generated serializer.
    // ReSharper disable NotAccessedPositionalProperty.Local
    private sealed record LocalFileTranscribeRequest(
        string Path,
        string? Language,
        IReadOnlyList<string> LanguageHints,
        string? Task,
        string? TargetLanguage,
        string? ResponseFormat,
        string? Prompt,
        string? Engine,
        string? Model,
        bool AwaitDownload,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ApplyCorrections
    );
    // ReSharper restore NotAccessedPositionalProperty.Local

    // Source-generated so the published CLI can be trimmed: the reflection-based
    // serializer roots types the linker can't see and warns (IL2026).
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(LocalFileTranscribeRequest))]
    private partial class TranscribeJsonContext : JsonSerializerContext;
}