using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.Cli.Models;
using TypeWhisper.Cli.Output;
using TypeWhisper.Cli.Services;

namespace TypeWhisper.Cli.Commands;

/// <summary>
///     Implements <c>typewhisper transcribe &lt;file|-&gt;</c>: uploads an audio
///     file (or stdin) to the API and prints the transcript or JSON response.
/// </summary>
internal static class TranscribeCommand
{
    // Mirrors the server's MaxTranscribeRequestBytes (HttpApiService): the API
    // rejects larger uploads, so there is no point buffering past this.
    private const long MaxStdinBytes = 100L * 1024 * 1024;

    public static async Task<int> RunAsync(ApiClient api, CliOptions options)
    {
        if (!string.IsNullOrEmpty(options.Language) && options.LanguageHints.Count > 0)
        {
            return ConsoleOutput.Error("--language and --language-hint cannot be used together.");
        }

        var file = options.Positionals.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(file))
        {
            return ConsoleOutput.Error("Usage: typewhisper transcribe <file|->");
        }

        Stream audioStream;
        string fileName;

        if (file == "-")
        {
            // Buffer stdin so we can magic-sniff the first bytes and still
            // forward the whole stream to the API. Audio uploads from
            // dictation pipelines fit easily in memory at MaxTranscribeRequestBytes;
            // cap the read at that limit so an unbounded pipe can't OOM the CLI.
            var buffer = new MemoryStream();
            var stdin = Console.OpenStandardInput();
            var chunk = new byte[81920];
            int read;
            while ((read = await stdin.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > MaxStdinBytes)
                {
                    return ConsoleOutput.Error(
                        $"stdin audio exceeds the {MaxStdinBytes / (1024 * 1024)} MB limit."
                    );
                }

                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            audioStream = buffer;
            fileName = $"stdin.{StdinAudioSniffer.Detect(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))}";
        }
        else
        {
            if (!File.Exists(file))
            {
                return ConsoleOutput.Error($"File not found: {file}");
            }

            try
            {
                audioStream = File.OpenRead(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ConsoleOutput.Error($"Could not open file: {ex.Message}");
            }

            fileName = Path.GetFileName(file);
        }

        try
        {
            await using (audioStream)
            {
                using var content = new MultipartFormDataContent();
                var fileContent = new StreamContent(audioStream);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(
                    "application/octet-stream"
                );
                content.Add(fileContent, "file", fileName);

                AddString(content, "language", options.Language);
                foreach (var hint in options.LanguageHints)
                {
                    AddString(content, "language_hint", hint);
                }

                AddString(content, "task", options.Task);
                AddString(content, "target_language", options.TranslateTo);
                AddString(content, "response_format", options.ResponseFormat);
                AddString(content, "prompt", options.Prompt);
                AddString(content, "engine", options.Engine);
                AddString(content, "model", options.Model);

                var path = options.AwaitDownload
                    ? "/v1/transcribe?await_download=1"
                    : "/v1/transcribe";
                var requestBudget = options.AwaitDownload
                    ? TimeSpan.FromMinutes(15)
                    : TimeSpan.FromMinutes(5);
                using var requestCts = new CancellationTokenSource(requestBudget);

                HttpResponseMessage response;
                string body;
                try
                {
                    response = await api.TranscribeHttp.PostAsync(
                        $"{api.BaseUrl}{path}",
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
        }
        catch (HttpRequestException)
        {
            return ConsoleOutput.Error("TypeWhisper is not running or API server is disabled.");
        }
    }

    private static void AddString(MultipartFormDataContent content, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            content.Add(new StringContent(value), name);
        }
    }
}