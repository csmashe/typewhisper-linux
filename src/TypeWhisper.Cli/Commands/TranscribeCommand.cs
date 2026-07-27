using System.Text;
using System.Text.Json;
using TypeWhisper.Cli.Models;
using TypeWhisper.Cli.Output;
using TypeWhisper.Cli.Services;

namespace TypeWhisper.Cli.Commands;

/// <summary>
///     Implements <c>typewhisper transcribe &lt;file|-&gt;</c>: passes a local audio
///     path (spooling stdin to a private file) to the API and prints the transcript
///     or JSON response.
/// </summary>
internal static class TranscribeCommand
{
    // Longest magic-byte window StdinAudioSniffer.Detect inspects (RIFF/WAVE).
    private const int SniffHeadBytes = 12;

    private const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static Task<int> RunAsync(ApiClient api, CliOptions options)
    {
        return RunAsync(api, options, Console.OpenStandardInput());
    }

    internal static async Task<int> RunAsync(
        ApiClient api,
        CliOptions options,
        Stream stdin
    )
    {
        if (!string.IsNullOrEmpty(options.Language) && options.LanguageHints.Count > 0)
        {
            return ConsoleOutput.Error("--language and --language-hint cannot be used together.");
        }

        var file = options.Positionals.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(file))
        {
            return ConsoleOutput.Error("Usage: typewhisper-cli transcribe <file|->");
        }

        string? spoolPath = null;
        try
        {
            string localPath;
            if (file == "-")
            {
                try
                {
                    spoolPath = await SpoolStdinAsync(stdin);
                    if (spoolPath is null)
                    {
                        // Multipart already 400s empty bodies; local-file has no such
                        // check and would spawn ffmpeg on nothing, returning a bare 500.
                        return ConsoleOutput.Error("Empty audio data on stdin.");
                    }

                    localPath = spoolPath;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
                Clean(options.Language),
                [.. options.LanguageHints.Select(Clean).OfType<string>()],
                Clean(options.Task),
                Clean(options.TranslateTo),
                Clean(options.ResponseFormat),
                Clean(options.Prompt),
                Clean(options.Engine),
                Clean(options.Model),
                options.AwaitDownload
            );
            using var content = new StringContent(
                JsonSerializer.Serialize(request, s_jsonOptions),
                Encoding.UTF8,
                "application/json"
            );
            var requestBudget = options.AwaitDownload
                ? TimeSpan.FromMinutes(15)
                : TimeSpan.FromMinutes(5);
            using var requestCts = new CancellationTokenSource(requestBudget);

            HttpResponseMessage response;
            string body;
            try
            {
                response = await api.TranscribeHttp.PostAsync(
                    $"{api.BaseUrl}/v1/transcribe/local-file",
                    content,
                    requestCts.Token
                );
                body = await response.Content.ReadAsStringAsync(requestCts.Token);
            }
            catch (OperationCanceledException)
            {
                return ConsoleOutput.Error(
                    options.AwaitDownload
                        ? "Transcription timed out while waiting for model download."
                        : "Transcription timed out."
                );
            }

            if (!response.IsSuccessStatusCode)
            {
                return ConsoleOutput.Error(
                    $"Transcription failed ({(int)response.StatusCode}): {JsonFormatting.ExtractErrorMessage(body)}"
                );
            }

            if (options.Json)
            {
                Console.WriteLine(JsonFormatting.PrettyJson(body));
                return 0;
            }

            using var doc = JsonDocument.Parse(body);
            Console.WriteLine(JsonFormatting.Prop(doc.RootElement, "text"));
            return 0;
        }
        catch (HttpRequestException)
        {
            return ConsoleOutput.Error("TypeWhisper is not running or API server is disabled.");
        }
        finally
        {
            if (spoolPath is not null)
            {
                File.Delete(spoolPath);
            }
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
    private static async Task<string?> SpoolStdinAsync(Stream stdin)
    {
        // A pipe may satisfy a read with fewer bytes than were asked for, so fill
        // the whole sniff window before detecting; otherwise a short first read
        // mis-detects the container as the "wav" default.
        var head = new byte[SniffHeadBytes];
        var headLength = 0;
        while (headLength < head.Length)
        {
            var headRead = await stdin.ReadAsync(head.AsMemory(headLength));
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
            await spool.WriteAsync(head.AsMemory(0, headLength));

            // The local-file route has no audio-body limit, so stream until EOF and
            // let available temporary storage be the natural bound.
            var chunk = new byte[81920];
            int read;
            while ((read = await stdin.ReadAsync(chunk)) > 0)
            {
                await spool.WriteAsync(chunk.AsMemory(0, read));
            }

            return spoolPath;
        }
        catch
        {
            File.Delete(spoolPath);
            throw;
        }
    }

    // Every property is read reflectively by JsonSerializer when the request body is
    // written, which ReSharper cannot see.
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
        bool AwaitDownload
    );
    // ReSharper restore NotAccessedPositionalProperty.Local
}
