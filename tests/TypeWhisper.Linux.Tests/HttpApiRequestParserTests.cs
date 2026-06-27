using System.Collections.Specialized;
using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public class HttpApiRequestParserTests
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void ParseTranscribe_ReadsMultipartFileAndFields()
    {
        const string boundary = "Boundary123";
        var body = Multipart(
            boundary,
            ("language_hint", null, null, "de"u8.ToArray()),
            ("language_hint", null, null, "en"u8.ToArray()),
            ("task", null, null, "translate"u8.ToArray()),
            ("target_language", null, null, "fr"u8.ToArray()),
            ("response_format", null, null, "verbose_json"u8.ToArray()),
            ("prompt", null, null, "Project names"u8.ToArray()),
            ("engine", null, null, "groq"u8.ToArray()),
            ("model", null, null, "whisper-large-v3"u8.ToArray()),
            ("file", "audio.wav", "audio/wav", [1, 2, 3, 4])
        );

        var request = new HttpApiRequest(
            "POST",
            "/v1/transcribe",
            new NameValueCollection { ["await_download"] = "1" },
            new Dictionary<string, string>
            {
                ["content-type"] = $"multipart/form-data; boundary={boundary}"
            },
            body
        );

        var parsed = HttpApiRequestParser.ParseTranscribe(request);

        Assert.Equal([1, 2, 3, 4], parsed.AudioData);
        Assert.Equal("wav", parsed.FileExtension);
        Assert.Null(parsed.Language);
        Assert.Equal(["de", "en"], parsed.LanguageHints);
        Assert.Equal(TranscriptionTask.Translate, parsed.Task);
        Assert.Equal("fr", parsed.TargetLanguage);
        Assert.Equal("verbose_json", parsed.ResponseFormat);
        Assert.Equal("Project names", parsed.Prompt);
        Assert.Equal("groq", parsed.Engine);
        Assert.Equal("whisper-large-v3", parsed.Model);
        Assert.True(parsed.AwaitDownload);
    }

    [Fact]
    public void ParseTranscribe_ReadsRawBodyHeaders()
    {
        var request = new HttpApiRequest(
            "POST",
            "/v1/transcribe",
            new NameValueCollection(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["content-type"] = "audio/mpeg",
                ["x-language-hints"] = "de, en",
                ["x-task"] = "translate",
                ["x-target-language"] = "es",
                ["x-response-format"] = "verbose_json",
                ["x-prompt"] = "Names",
                ["x-engine"] = "openai",
                ["x-model"] = "gpt-4o-transcribe"
            },
            [9, 8, 7]
        );

        var parsed = HttpApiRequestParser.ParseTranscribe(request);

        Assert.Equal("mp3", parsed.FileExtension);
        Assert.Equal(["de", "en"], parsed.LanguageHints);
        Assert.Equal(TranscriptionTask.Translate, parsed.Task);
        Assert.Equal("es", parsed.TargetLanguage);
        Assert.Equal("verbose_json", parsed.ResponseFormat);
        Assert.Equal("Names", parsed.Prompt);
        Assert.Equal("openai", parsed.Engine);
        Assert.Equal("gpt-4o-transcribe", parsed.Model);
    }

    [Fact]
    public void ParseTranscribe_RejectsLanguageAndHintsTogether()
    {
        const string boundary = "Boundary123";
        var body = Multipart(
            boundary,
            ("language", null, null, "de"u8.ToArray()),
            ("language_hint", null, null, "en"u8.ToArray()),
            ("file", "audio.wav", "audio/wav", [1])
        );

        var request = new HttpApiRequest(
            "POST",
            "/v1/transcribe",
            new NameValueCollection(),
            new Dictionary<string, string>
            {
                ["content-type"] = $"multipart/form-data; boundary={boundary}"
            },
            body
        );

        var ex = Assert.Throws<HttpApiRequestException>(() =>
            HttpApiRequestParser.ParseTranscribe(request)
        );
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("language", ex.Message);
    }

    [Fact]
    public void ParseTranscribe_RejectsMultipartWithoutFile()
    {
        const string boundary = "Boundary123";
        var body = Multipart(boundary, ("language", null, null, "de"u8.ToArray()));
        var request = new HttpApiRequest(
            "POST",
            "/v1/transcribe",
            new NameValueCollection(),
            new Dictionary<string, string>
            {
                ["content-type"] = $"multipart/form-data; boundary={boundary}"
            },
            body
        );

        var ex = Assert.Throws<HttpApiRequestException>(() =>
            HttpApiRequestParser.ParseTranscribe(request)
        );
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("file", ex.Message);
    }

    [Fact]
    public void ParseTranscribeLocalFile_DeserializesFullBody()
    {
        const string json = """
            {
              "path": "/tmp/clip.wav",
              "language": "en",
              "language_hints": ["de", "fr"],
              "task": "translate",
              "target_language": "es",
              "response_format": "verbose_json",
              "prompt": "Names",
              "engine": "groq",
              "model": "whisper-large-v3",
              "await_download": true
            }
            """;
        var parsed = JsonSerializer.Deserialize<LocalFileTranscribeRequest>(json, s_jsonOptions)!;

        Assert.Equal("/tmp/clip.wav", parsed.Path);
        Assert.Equal("en", parsed.Language);
        Assert.Equal(["de", "fr"], parsed.LanguageHints);
        Assert.Equal("translate", parsed.Task);
        Assert.Equal("es", parsed.TargetLanguage);
        Assert.Equal("verbose_json", parsed.ResponseFormat);
        Assert.Equal("Names", parsed.Prompt);
        Assert.Equal("groq", parsed.Engine);
        Assert.Equal("whisper-large-v3", parsed.Model);
        Assert.True(parsed.AwaitDownload);
    }

    [Fact]
    public void ParseDictionaryTermDelete_RequiresTerm()
    {
        var withTerm = JsonSerializer.Deserialize<DictionaryTermDeleteRequest>(
            """{"term":"FooCorp"}""",
            s_jsonOptions
        );
        Assert.Equal("FooCorp", withTerm!.Term);

        var empty = JsonSerializer.Deserialize<DictionaryTermDeleteRequest>("{}", s_jsonOptions);
        Assert.Null(empty!.Term);
    }

    [Fact]
    public void ParseCorrectionUpsert_AcceptsOptionalCaseSensitive()
    {
        var parsed = JsonSerializer.Deserialize<CorrectionUpsertRequest>(
            """{"original":"teh","replacement":"the"}""",
            s_jsonOptions
        );
        Assert.Equal("teh", parsed!.Original);
        Assert.Equal("the", parsed.Replacement);
        Assert.Null(parsed.CaseSensitive);

        var withFlag = JsonSerializer.Deserialize<CorrectionUpsertRequest>(
            """{"original":"teh","replacement":"the","case_sensitive":true}""",
            s_jsonOptions
        );
        Assert.True(withFlag!.CaseSensitive);
    }

    private static byte[] Multipart(
        string boundary,
        params (string Name, string? FileName, string? ContentType, byte[] Data)[] parts
    )
    {
        using var body = new MemoryStream();
        foreach (var part in parts)
        {
            Write(body, $"--{boundary}\r\n");
            var disposition = $"Content-Disposition: form-data; name=\"{part.Name}\"";
            if (part.FileName is not null)
            {
                disposition += $"; filename=\"{part.FileName}\"";
            }

            Write(body, disposition + "\r\n");
            if (part.ContentType is not null)
            {
                Write(body, $"Content-Type: {part.ContentType}\r\n");
            }

            Write(body, "\r\n");
            body.Write(part.Data);
            Write(body, "\r\n");
        }

        Write(body, $"--{boundary}--\r\n");
        return body.ToArray();
    }

    private static void Write(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
    }
}