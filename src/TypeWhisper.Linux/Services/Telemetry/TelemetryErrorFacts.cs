using System.ComponentModel;
using System.Globalization;
using System.Net.Sockets;

namespace TypeWhisper.Linux.Services.Telemetry;

internal static class TelemetryErrorFacts
{
    public static void Apply(SentryEvent e)
    {
        if (e.Exception is not { } outermost)
        {
            return;
        }

        string? httpStatus = null;
        string? socketError = null;
        string? nativeError = null;
        Exception? innermost = null;
        var innermostDepth = -1;
        Walk(outermost, 0);

        e.SetTag("error.hresult", $"0x{outermost.HResult:X8}");
        if (httpStatus is not null)
        {
            e.SetTag("http.status", httpStatus);
        }

        if (socketError is not null)
        {
            e.SetTag("socket.error", socketError);
        }

        if (nativeError is not null)
        {
            e.SetTag("error.native_code", nativeError);
        }

        if (innermost is null || innermost.GetType() == outermost.GetType()
            || innermost.GetType().FullName is not { } innerType)
        {
            return;
        }

        e.SetTag("error.inner_type", innerType);
        e.SetTag("error.inner_hresult", $"0x{innermost.HResult:X8}");
        return;

        void Walk(Exception? exception, int depth)
        {
            while (exception is not null && depth < 32)
            {
                if (httpStatus is null && exception is HttpRequestException { StatusCode: { } status })
                {
                    httpStatus = ((int)status).ToString(CultureInfo.InvariantCulture);
                }

                if (exception is SocketException socket)
                {
                    socketError ??= socket.SocketErrorCode.ToString();
                }
                else if (nativeError is null && exception is Win32Exception { NativeErrorCode: >= 0 } native)
                {
                    nativeError = native.NativeErrorCode.ToString(CultureInfo.InvariantCulture);
                }

                if (exception is AggregateException aggregate)
                {
                    foreach (var inner in aggregate.InnerExceptions)
                    {
                        Walk(inner, depth + 1);
                    }

                    return;
                }

                if (depth > innermostDepth)
                {
                    innermost = exception;
                    innermostDepth = depth;
                }

                exception = exception.InnerException;
                depth++;
            }
        }
    }
}
