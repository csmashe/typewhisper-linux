using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Telemetry;
using Xunit;

namespace TypeWhisper.Linux.Tests.Telemetry;

[CollectionDefinition("SentrySdk", DisableParallelization = true)]
public sealed class SentrySdkCollection;

internal static class RecordedEnvelope
{
    public static bool ContainsException(string envelope, string type, string value)
    {
        foreach (var line in envelope.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var json = JsonDocument.Parse(line);
            if (!json.RootElement.TryGetProperty("exception", out var exceptions))
            {
                continue;
            }

            // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator -- the loop is clearer and the LINQ form changes the enumerator.
            foreach (var exception in exceptions.GetProperty("values").EnumerateArray())
            {
                if (exception.GetProperty("type").GetString() == type
                    && exception.GetProperty("value").GetString() == value)
                {
                    return true;
                }
            }
        }

        return false;
    }
}

internal sealed class RecordingTransport : ITransport
{
    public ConcurrentQueue<string> Envelopes { get; } = new();

    public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream();
        await envelope.SerializeAsync(stream, logger: null, cancellationToken: cancellationToken);
        Envelopes.Enqueue(Encoding.UTF8.GetString(stream.ToArray()));
    }
}

internal sealed class FakeSettingsService(AppSettings? initial = null) : ISettingsService
{
    public AppSettings Current { get; private set; } = initial ?? AppSettings.Default;

    public event Action<AppSettings>? SettingsChanged;
    public AppSettings Load() => Current;

    public void Save(AppSettings settings) => Change(settings);
    public AppSettings Update(Func<AppSettings, AppSettings> mutate)
    {
        Change(mutate(Current));
        return Current;
    }

    public void Change(AppSettings settings)
    {
        Current = settings;
        SettingsChanged?.Invoke(settings);
    }
}

internal sealed class RecordingReporter : IDiagnosticsReporter
{
    public List<(string Name, string Operation, RecordingOperation Value)> Operations { get; } = [];

    public void CaptureException(
        Exception exception, string operation, IReadOnlyDictionary<string, string>? tags = null)
    {
    }

    public IDiagnosticsOperation BeginOperation(string name, string operation)
    {
        var value = new RecordingOperation();
        Operations.Add((name, operation, value));
        return value;
    }
}

internal sealed class RecordingOperation : IDiagnosticsOperation
{
    public List<(string Operation, string? Description, RecordingOperation Value)> Children { get; } = [];
    public Dictionary<string, string> Tags { get; } = [];
    public Dictionary<string, (double Value, string Unit)> Measurements { get; } = [];
    public List<DiagnosticsOutcome> Finishes { get; } = [];

    public IDiagnosticsOperation StartChild(string operation, string? description = null)
    {
        var value = new RecordingOperation();
        Children.Add((operation, description, value));
        return value;
    }

    public void SetTag(string key, string value) => Tags[key] = value;

    public void SetMeasurement(string name, double value, string unit) => Measurements[name] = (value, unit);

    public void Finish(DiagnosticsOutcome outcome) => Finishes.Add(outcome);

    public void Dispose() => Finish(DiagnosticsOutcome.Ok);
}

internal static class ExceptionSamples
{
    public static InvalidOperationException AppThrown(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    public static FileNotFoundException RuntimeFileFailure(string directory)
    {
        return Assert.Throws<FileNotFoundException>(() =>
            File.ReadAllText(Path.Join(directory, $"sentry-missing-{Guid.NewGuid():N}.txt")));
    }
}

internal sealed class RecordingHttpHandler : HttpMessageHandler
{
    public ConcurrentQueue<(HttpMethod Method, string Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var content = request.Content!;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        await using var body = content.Headers.ContentEncoding.Contains("gzip")
            ? new GZipStream(stream, CompressionMode.Decompress) : stream;
        using var reader = new StreamReader(body);
        Requests.Enqueue((request.Method, await reader.ReadToEndAsync(cancellationToken)));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
        };
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return SendAsync(request, cancellationToken).GetAwaiter().GetResult();
    }
}

internal sealed class QueueUntilShutdownLogger : IDiagnosticLogger, IDisposable
{
    private readonly ManualResetEventSlim _shutdown = new(false);

    public bool TimedOut { get; private set; }

    public bool Queued { get; private set; }

    public void Reset()
    {
        Queued = false;
        _shutdown.Reset();
    }

    public bool IsEnabled(SentryLevel level) => true;

    public void Log(SentryLevel logLevel, string message, Exception? exception = null, params object?[] args)
    {
        switch (message)
        {
            case "BackgroundWorker Started.":
                Queued = true;
                TimedOut = !_shutdown.Wait(TimeSpan.FromSeconds(10));
                break;
            case "Disposing the Hub.":
                _shutdown.Set();
                break;
        }
    }

    public void Dispose() => _shutdown.Dispose();
}
