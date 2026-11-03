using System.Net;

namespace TypeWhisper.Linux.Services.Telemetry;

internal sealed class TelemetryConsentGate(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private volatile bool _open = true;

    public bool IsOpen => _open;

    public void Close() => _open = false;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return _open ? base.SendAsync(request, cancellationToken) : Task.FromResult(Discard(request));
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return _open ? base.Send(request, cancellationToken) : Discard(request);
    }

    private static HttpResponseMessage Discard(HttpRequestMessage request)
    {
        // Acknowledge queued reports locally so opting out cannot flush them to Sentry.
        request.Content?.Dispose();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
        };
    }
}
