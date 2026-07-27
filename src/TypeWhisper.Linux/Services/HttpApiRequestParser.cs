using System.Collections.Specialized;
using System.Text;
using Microsoft.AspNetCore.Http;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services;

internal sealed record HttpApiRequest(
    // ReSharper disable once NotAccessedPositionalProperty.Global  part of the parsed-request data shape (routing currently keys off QueryString/Body)
    string Method,
    // ReSharper disable once NotAccessedPositionalProperty.Global  part of the parsed-request data shape (routing currently keys off QueryString/Body)
    string Path,
    NameValueCollection QueryString,
    IReadOnlyDictionary<string, string> Headers,
    ReadOnlyMemory<byte> Body
);

internal sealed class HttpApiRequestException : Exception
{
    public HttpApiRequestException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

internal sealed record TranscribeApiRequest(
    ReadOnlyMemory<byte> AudioData,
    string FileExtension,
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

internal sealed record MultipartPart(
    string Name,
    string? FileName,
    string? ContentType,
    ReadOnlyMemory<byte> Data
);

internal sealed record LocalFileTranscribeRequest(
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

internal sealed record CorrectionUpsertRequest(
    string Original,
    string Replacement,
    bool? CaseSensitive
);

internal sealed record CorrectionDeleteRequest(string Original);

internal sealed record DictionaryTermDeleteRequest(string Term);

/// <summary>
///     Hand-rolled multipart/form-data parser for the local HTTP API. Custom
///     so the transport-neutral request shape and exact parsing behavior stay
///     shared across the TCP and Unix-socket listeners. Body size is capped
///     while streaming so a malicious / runaway client cannot OOM the
///     dictation host.
/// </summary>
internal static class HttpApiRequestParser
{
    public static async Task<HttpApiRequest> FromHttpContextAsync(
        HttpContext context,
        long maxBytes,
        CancellationToken ct
    )
    {
        var request = context.Request;
        var body = await ReadBodyAsync(
            request.Body,
            request.ContentLength ?? -1,
            maxBytes,
            ct
        );

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.Headers)
        {
            headers[key] = value.ToString();
        }

        var queryString = new NameValueCollection();
        foreach (var (key, values) in request.Query)
        {
            foreach (var value in values)
            {
                queryString.Add(key, value);
            }
        }

        return new HttpApiRequest(
            request.Method,
            request.Path.Value ?? "",
            queryString,
            headers,
            body
        );
    }

    internal static async Task<ReadOnlyMemory<byte>> ReadBodyAsync(
        Stream input,
        long declaredLength,
        long maxBytes,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfLessThan(declaredLength, -1);

        if (maxBytes is < 0 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        if (declaredLength > maxBytes)
        {
            throw new HttpApiRequestException(413, "Request body too large");
        }

        var initialCapacity = declaredLength >= 0 ? checked((int)declaredLength) : 0;
        using var buffer = new MemoryStream(initialCapacity);
        try
        {
            await using var limited = new LimitedReadStream(input, maxBytes);
            await limited.CopyToAsync(buffer, ct);
        }
        catch (RequestBodyTooLargeException)
        {
            throw new HttpApiRequestException(413, "Request body too large");
        }

        if (!buffer.TryGetBuffer(out var segment))
        {
            throw new InvalidOperationException("Request body buffer is not publicly visible.");
        }

        return new ReadOnlyMemory<byte>(
            segment.Array!,
            segment.Offset,
            checked((int)buffer.Length)
        );
    }

    public static TranscribeApiRequest ParseTranscribe(HttpApiRequest request)
    {
        var contentType = Header(request.Headers, "content-type") ?? "";
        ReadOnlyMemory<byte> audioData;
        string fileExtension;
        string? language;
        var languageHints = new List<string>();
        TranscriptionTask task;
        string? targetLanguage;
        string responseFormat;
        string? prompt;
        string? engine;
        string? model;

        if (contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            var boundary =
                ExtractBoundary(contentType)
                ?? throw new HttpApiRequestException(400, "Missing multipart boundary");
            var parts = ParseMultipart(request.Body, boundary);
            var filePart =
                parts.FirstOrDefault(p => p.Name == "file")
                ?? throw new HttpApiRequestException(
                    400,
                    "Missing 'file' part in multipart form data"
                );

            audioData = filePart.Data;
            fileExtension =
                ExtensionFromFileName(filePart.FileName)
                ?? ExtensionFromMime(filePart.ContentType)
                ?? "wav";

            language = Field(parts, "language");
            languageHints.AddRange(Fields(parts, "language_hint"));
            task = ParseTask(Field(parts, "task"));
            targetLanguage = Field(parts, "target_language");
            responseFormat = Field(parts, "response_format") ?? "json";
            prompt = Field(parts, "prompt");
            engine = Field(parts, "engine");
            model = Field(parts, "model");
        }
        else if (request.Body.Length > 0)
        {
            audioData = request.Body;
            fileExtension = ExtensionFromMime(contentType) ?? "wav";
            language = Clean(Header(request.Headers, "x-language"));
            languageHints.AddRange(
                (Header(request.Headers, "x-language-hints") ?? "")
                .Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                )
                .Where(v => !string.IsNullOrWhiteSpace(v))
            );
            task = ParseTask(Header(request.Headers, "x-task"));
            targetLanguage = Clean(Header(request.Headers, "x-target-language"));
            responseFormat = Clean(Header(request.Headers, "x-response-format")) ?? "json";
            prompt = Clean(Header(request.Headers, "x-prompt"));
            engine = Clean(Header(request.Headers, "x-engine"));
            model = Clean(Header(request.Headers, "x-model"));
        }
        else
        {
            throw new HttpApiRequestException(400, "No audio data provided");
        }

        if (audioData.Length == 0)
        {
            throw new HttpApiRequestException(400, "Empty audio data");
        }

        if (!string.IsNullOrWhiteSpace(language) && languageHints.Count > 0)
        {
            throw new HttpApiRequestException(
                400,
                "Use either 'language' or 'language_hint', not both"
            );
        }

        var awaitDownload =
            string.Equals(request.QueryString["await_download"], "1", StringComparison.Ordinal)
            || string.Equals(
                request.QueryString["await_download"],
                "true",
                StringComparison.OrdinalIgnoreCase
            );

        return new TranscribeApiRequest(
            audioData,
            fileExtension,
            language,
            languageHints,
            task,
            targetLanguage,
            responseFormat,
            prompt,
            engine,
            model,
            awaitDownload
        );
    }

    // ReSharper disable once MemberCanBePrivate.Global
    // only used internally, but privatizing surfaces CA1859 (return-type) which can't be fixed without altering the signature
    public static IReadOnlyList<MultipartPart> ParseMultipart(
        ReadOnlyMemory<byte> body,
        string boundary
    )
    {
        var boundaryBytes = Encoding.UTF8.GetBytes("--" + boundary);
        var doubleCrlf = "\r\n\r\n"u8;
        var parts = new List<MultipartPart>();
        var searchStart = 0;
        var bodySpan = body.Span;

        while (searchStart < bodySpan.Length)
        {
            var boundaryStart = IndexOfDelimiter(bodySpan, boundaryBytes, searchStart);
            if (boundaryStart < 0)
            {
                break;
            }

            var afterBoundary = boundaryStart + boundaryBytes.Length;
            if (
                afterBoundary + 1 < bodySpan.Length
                && bodySpan[afterBoundary] == (byte)'-'
                && bodySpan[afterBoundary + 1] == (byte)'-'
            )
            {
                break;
            }

            // Already validated as part of the delimiter; skipping it keeps the header block clean.
            var partHeaderStart = SkipTransportPadding(bodySpan, afterBoundary);
            if (
                partHeaderStart + 1 < bodySpan.Length
                && bodySpan[partHeaderStart] == (byte)'\r'
                && bodySpan[partHeaderStart + 1] == (byte)'\n'
            )
            {
                partHeaderStart += 2;
            }

            var headerEnd = IndexOf(bodySpan, doubleCrlf, partHeaderStart);
            if (headerEnd < 0)
            {
                break;
            }

            var partBodyStart = headerEnd + doubleCrlf.Length;
            var nextBoundary = IndexOfDelimiter(bodySpan, boundaryBytes, partBodyStart);
            if (nextBoundary < 0)
            {
                break;
            }

            var partBodyEnd = nextBoundary;
            if (
                partBodyEnd >= 2
                && bodySpan[partBodyEnd - 2] == (byte)'\r'
                && bodySpan[partBodyEnd - 1] == (byte)'\n'
            )
            {
                partBodyEnd -= 2;
            }

            if (partBodyEnd < partBodyStart)
            {
                searchStart = nextBoundary;
                continue;
            }

            var headers = Encoding.UTF8.GetString(
                bodySpan.Slice(partHeaderStart, headerEnd - partHeaderStart)
            );
            var parsedHeaders = ParsePartHeaders(headers);
            if (!string.IsNullOrEmpty(parsedHeaders.Name))
            {
                parts.Add(
                    new MultipartPart(
                        parsedHeaders.Name,
                        parsedHeaders.FileName,
                        parsedHeaders.ContentType,
                        body.Slice(partBodyStart, partBodyEnd - partBodyStart)
                    )
                );
            }

            searchStart = nextBoundary;
        }

        return parts;
    }

    private static string? ExtractBoundary(string contentType)
    {
        foreach (var part in contentType.Split(';', StringSplitOptions.TrimEntries))
        {
            if (!part.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var boundary = part["boundary=".Length..].Trim();
            if (boundary is ['"', _, ..] && boundary[^1] == '"')
            {
                boundary = boundary[1..^1];
            }

            return string.IsNullOrWhiteSpace(boundary) ? null : boundary;
        }

        return null;
    }

    private static (string Name, string? FileName, string? ContentType) ParsePartHeaders(
        string headers
    )
    {
        string? name = null;
        string? fileName = null;
        string? contentType = null;

        foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var headerName = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (headerName.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase))
            {
                name = ExtractDispositionParameter(value, "name");
                fileName =
                    ExtractDispositionParameter(value, "filename")
                    ?? ExtractDispositionParameter(value, "filename*");
            }
            else if (headerName.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = value;
            }
        }

        return (name ?? "", fileName, contentType);
    }

    private static string? ExtractDispositionParameter(string value, string key)
    {
        foreach (var part in value.Split(';', StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            var partKey = part[..equals].Trim();
            if (!partKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parameterValue = part[(equals + 1)..].Trim();
            if (parameterValue is ['"', _, ..] && parameterValue[^1] == '"')
            {
                parameterValue = parameterValue[1..^1];
            }

            if (key.EndsWith('*') && parameterValue.Contains("''", StringComparison.Ordinal))
            {
                parameterValue = parameterValue[
                    (parameterValue.IndexOf("''", StringComparison.Ordinal) + 2)..
                ];
            }

            return string.IsNullOrWhiteSpace(parameterValue)
                ? null
                : Uri.UnescapeDataString(parameterValue);
        }

        return null;
    }

    private static string? Header(IReadOnlyDictionary<string, string> headers, string name)
    {
        return headers.GetValueOrDefault(name);
    }

    private static string? Field(IEnumerable<MultipartPart> parts, string name)
    {
        return parts
            .Where(p => p.Name == name)
            .Select(p => Clean(Encoding.UTF8.GetString(p.Data.Span)))
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    private static IEnumerable<string> Fields(IEnumerable<MultipartPart> parts, string name)
    {
        return parts
            .Where(p => p.Name == name)
            .Select(p => Clean(Encoding.UTF8.GetString(p.Data.Span)))
            .Where(v => !string.IsNullOrWhiteSpace(v))!;
    }

    private static string? Clean(string? value)
    {
        var cleaned = value?.Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static TranscriptionTask ParseTask(string? value)
    {
        return string.Equals(value?.Trim(), "translate", StringComparison.OrdinalIgnoreCase)
            ? TranscriptionTask.Translate
            : TranscriptionTask.Transcribe;
    }

    private static string? ExtensionFromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var extension = Path.GetExtension(fileName).TrimStart('.').Trim();
        return string.IsNullOrWhiteSpace(extension) ? null : extension.ToLowerInvariant();
    }

    private static string? ExtensionFromMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime))
        {
            return null;
        }

        var lower = mime.ToLowerInvariant();
        if (lower.Contains("wav") || lower.Contains("wave"))
        {
            return "wav";
        }

        if (lower.Contains("mp3") || lower.Contains("mpeg"))
        {
            return "mp3";
        }

        if (lower.Contains("m4a") || lower.Contains("mp4"))
        {
            return "m4a";
        }

        if (lower.Contains("flac"))
        {
            return "flac";
        }

        if (lower.Contains("ogg"))
        {
            return "ogg";
        }

        if (lower.Contains("aac"))
        {
            return "aac";
        }

        return lower.Contains("webm") ? "webm" : null;
    }

    /// <summary>
    ///     Finds the next real delimiter, skipping boundary-looking bytes inside a part body.
    ///     RFC 2046 requires a preceding CRLF and a CRLF or "--" suffix; without that check a
    ///     binary payload containing the boundary text truncates the part it belongs to.
    /// </summary>
    private static int IndexOfDelimiter(
        ReadOnlySpan<byte> body,
        ReadOnlySpan<byte> boundaryBytes,
        int startIndex
    )
    {
        var from = startIndex;
        while (from < body.Length)
        {
            var at = IndexOf(body, boundaryBytes, from);
            if (at < 0)
            {
                return -1;
            }

            if (IsDelimiterAt(body, boundaryBytes, at))
            {
                return at;
            }

            from = at + 1;
        }

        return -1;
    }

    private static bool IsDelimiterAt(
        ReadOnlySpan<byte> body,
        ReadOnlySpan<byte> boundaryBytes,
        int at
    )
    {
        // Only the opening delimiter may sit at offset 0; every later one follows the CRLF
        // that ends the preceding part.
        if (at != 0 && (at < 2 || body[at - 2] != (byte)'\r' || body[at - 1] != (byte)'\n'))
        {
            return false;
        }

        var after = at + boundaryBytes.Length;
        if (after + 1 < body.Length && body[after] == (byte)'-' && body[after + 1] == (byte)'-')
        {
            // Closing delimiter — the epilogue after it still has to start on its own line.
            after += 2;
        }

        // RFC 2046 allows transport padding (SP/HTAB) between the boundary and its CRLF.
        after = SkipTransportPadding(body, after);
        return after >= body.Length
               || (after + 1 < body.Length
                   && body[after] == (byte)'\r'
                   && body[after + 1] == (byte)'\n');
    }

    private static int SkipTransportPadding(ReadOnlySpan<byte> body, int index)
    {
        while (index < body.Length && (body[index] == (byte)' ' || body[index] == (byte)'\t'))
        {
            index++;
        }

        return index;
    }

    private static int IndexOf(
        ReadOnlySpan<byte> haystack,
        ReadOnlySpan<byte> needle,
        int startIndex
    )
    {
        if (needle.Length == 0)
        {
            return startIndex;
        }

        var relativeIndex = haystack[startIndex..].IndexOf(needle);
        return relativeIndex < 0 ? -1 : startIndex + relativeIndex;
    }

    private sealed class LimitedReadStream(Stream inner, long maxBytes) : Stream
    {
        private long _bytesRead;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => _bytesRead;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            TrackBytes(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            TrackBytes(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            TrackBytes(read);
            return read;
        }

        public override int ReadByte()
        {
            var value = inner.ReadByte();
            if (value >= 0)
            {
                TrackBytes(1);
            }

            return value;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        private void TrackBytes(int read)
        {
            _bytesRead += read;
            if (_bytesRead > maxBytes)
            {
                throw new RequestBodyTooLargeException();
            }
        }
    }

    private sealed class RequestBodyTooLargeException : Exception;
}
