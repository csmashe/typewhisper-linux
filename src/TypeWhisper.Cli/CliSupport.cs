// Documented public helper surface for the CLI (connection resolution and request building).
// The shipped commands now go through Services/DiscoveryFileReader, Services/ApiClient and
// Services/StdinAudioSniffer, so nothing in-tree calls these any more and every member reads as
// unused or privatisable. Kept public as the documented request-shape reference for the HTTP API,
// so the "unused" family below is rot, not a mistake.
// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable NotAccessedPositionalProperty.Global
// ReSharper disable MemberCanBePrivate.Global

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypeWhisper.Cli;

/// <summary>
/// Represents cli connection options data.
/// </summary>
/// <param name="ApplicationDataRoot">Application data root supplied to the member.</param>
/// <param name="PortOverride">Port override supplied to the member.</param>
/// <param name="ApiTokenOverride">Api token override supplied to the member.</param>
/// <param name="EnvironmentApiToken">Environment api token supplied to the member.</param>
public sealed record CliConnectionOptions(
    string? ApplicationDataRoot = null,
    int? PortOverride = null,
    string? ApiTokenOverride = null,
    string? EnvironmentApiToken = null);

/// <summary>
/// Represents cli connection data.
/// </summary>
/// <param name="Port">Port supplied to the member.</param>
/// <param name="ApiToken">Api token supplied to the member.</param>
public sealed record CliConnection(int Port, string? ApiToken);

/// <summary>
/// Represents cli transcribe request data.
/// </summary>
/// <param name="FilePath">File path supplied to the member.</param>
/// <param name="Language">Language supplied to the member.</param>
/// <param name="LanguageHints">Language hints supplied to the member.</param>
/// <param name="Task">Task supplied to the member.</param>
/// <param name="TargetLanguage">Target language supplied to the member.</param>
/// <param name="Engine">Engine supplied to the member.</param>
/// <param name="Model">Model supplied to the member.</param>
/// <param name="AwaitDownload">Await download supplied to the member.</param>
public sealed record CliTranscribeRequest(
    string FilePath,
    string? Language,
    IReadOnlyList<string> LanguageHints,
    string Task,
    string? TargetLanguage,
    string? Engine,
    string? Model,
    bool AwaitDownload);

/// <summary>
/// Provides cli connection resolver behavior.
/// </summary>
public static partial class CliConnectionResolver
{
    // Mirrors AppSettings.ApiServerPort, the port the app's API server binds by default.
    // Duplicated rather than referenced: the CLI ships as a standalone trimmed single file
    // and deliberately takes no project references. Keep the two in step.
    private const int DefaultPort = 9876;

    /// <summary>
    /// Resolves the supplied input to a configured value.
    /// </summary>
    public static CliConnection Resolve(CliConnectionOptions options)
    {
        var appDirectory = Path.Join(
            options.ApplicationDataRoot
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TypeWhisper");

        var discovery = ReadDiscovery(Path.Join(appDirectory, "api-discovery.json"));
        var port = ValidatePort(options.PortOverride)
            ?? ValidatePort(discovery?.Port)
            ?? ValidatePort(ReadLegacyPort(Path.Join(appDirectory, "api-port")))
            ?? DefaultPort;
        var token = FirstNonBlank(
            options.ApiTokenOverride,
            options.EnvironmentApiToken,
            discovery?.Token);

        return new CliConnection(port, token);
    }

    /// <summary>
    /// Returns whether port in range.
    /// </summary>
    public static bool IsPortInRange(int port) => port is >= 1 and <= 65535;

    private static ApiDiscovery? ReadDiscovery(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            // Source-generated rather than reflective: the CLI publishes with TrimMode=full,
            // where reflection-based deserialization is a latent runtime failure (IL2026).
            return JsonSerializer.Deserialize(
                File.ReadAllText(path),
                DiscoveryJsonContext.Default.ApiDiscovery);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static int? ReadLegacyPort(string path)
    {
        try
        {
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var port))
                return port;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return null;
        }

        return null;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static int? ValidatePort(int? port) =>
        port is { } value && IsPortInRange(value) ? value : null;

    private sealed record ApiDiscovery
    {
        /// <summary>
        /// Gets or sets the version value.
        /// </summary>
        // ReSharper disable once UnusedAutoPropertyAccessor.Local -- carried so this record mirrors
        // the api-discovery.json shape. Resolution accepts any version, matching DiscoveryFileReader
        // on the live path, so nothing reads it back.
        public int Version { get; init; }
        /// <summary>
        /// Gets or sets the port value.
        /// </summary>
        public int Port { get; init; }
        /// <summary>
        /// Gets or sets the token value.
        /// </summary>
        public string? Token { get; init; }
    }

    // Source-generated so the published CLI can be trimmed: the reflection-based
    // serializer roots types the linker can't see and warns (IL2026).
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(ApiDiscovery))]
    private partial class DiscoveryJsonContext : JsonSerializerContext;
}

/// <summary>
/// Provides cli request builder behavior.
/// </summary>
public static class CliRequestBuilder
{
    /// <summary>
    /// Builds get.
    /// </summary>
    public static HttpRequestMessage BuildGet(string baseUrl, string path, string? apiToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(baseUrl, path));
        ApplyApiToken(request, apiToken);
        return request;
    }

    /// <summary>
    /// Builds transcribe local file.
    /// </summary>
    public static HttpRequestMessage BuildTranscribeLocalFile(
        string baseUrl,
        CliTranscribeRequest request,
        string? apiToken)
    {
        var path = request.AwaitDownload
            ? "/v1/transcribe/local-file?await_download=1"
            : "/v1/transcribe/local-file";
        var message = new HttpRequestMessage(HttpMethod.Post, BuildUri(baseUrl, path));
        ApplyApiToken(message, apiToken);

        var body = new Dictionary<string, object?>
        {
            ["path"] = request.FilePath,
            ["language"] = request.Language,
            ["language_hints"] = request.LanguageHints,
            ["task"] = request.Task,
            ["target_language"] = request.TargetLanguage,
            ["engine"] = request.Engine,
            ["model"] = request.Model,
        }
        .Where(pair => pair.Value is not null)
        .ToDictionary(pair => pair.Key, pair => pair.Value);

        // Hand-write rather than reflect over Dictionary<string, object?>, which TrimMode=full
        // cannot analyze (IL2026). Source-generating it is not an option either: the boxed
        // IReadOnlyList<string> value makes the dictionary polymorphic. Only strings and string
        // lists ever reach this body.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in body)
            {
                if (value is IReadOnlyList<string> list)
                {
                    writer.WriteStartArray(name);
                    foreach (var entry in list)
                        writer.WriteStringValue(entry);
                    writer.WriteEndArray();
                }
                else
                {
                    writer.WriteString(name, (string?)value);
                }
            }

            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(buffer.ToArray());
        message.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return message;
    }

    /// <summary>
    /// Applies api token.
    /// </summary>
    public static void ApplyApiToken(HttpRequestMessage request, string? apiToken)
    {
        if (!string.IsNullOrWhiteSpace(apiToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());
    }

    /// <summary>
    /// Builds stdin file name.
    /// </summary>
    public static string BuildStdinFileName(ReadOnlySpan<byte> audioBytes)
    {
        if (audioBytes.Length >= 12
            && audioBytes[..4].SequenceEqual("RIFF"u8)
            && audioBytes[8..12].SequenceEqual("WAVE"u8))
        {
            return "stdin.wav";
        }

        if (audioBytes.StartsWith("fLaC"u8))
            return "stdin.flac";

        if (audioBytes.StartsWith("OggS"u8))
            return "stdin.ogg";

        if (LooksLikeAdtsAac(audioBytes))
            return "stdin.aac";

        if (audioBytes.StartsWith("ID3"u8) || LooksLikeMp3Frame(audioBytes))
        {
            return "stdin.mp3";
        }

        return "stdin.wav";
    }

    private static bool LooksLikeAdtsAac(ReadOnlySpan<byte> audioBytes) =>
        audioBytes.Length >= 2
        && audioBytes[0] == 0xFF
        && (audioBytes[1] & 0xF0) == 0xF0
        && (audioBytes[1] & 0x06) == 0x00;

    private static bool LooksLikeMp3Frame(ReadOnlySpan<byte> audioBytes)
    {
        if (audioBytes.Length < 4 || audioBytes[0] != 0xFF || (audioBytes[1] & 0xE0) != 0xE0)
            return false;

        var version = (audioBytes[1] >> 3) & 0b11;
        var layer = (audioBytes[1] >> 1) & 0b11;

        return version != 0b01 && layer == 0b01;
    }

    private static Uri BuildUri(string baseUrl, string path) =>
        new(new Uri(baseUrl.TrimEnd('/')), path);
}
