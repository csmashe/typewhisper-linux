using System.Diagnostics;

namespace TypeWhisper.Linux.Services;

/// <summary>Validated HTTP(S) handoff through the host process supervisor.</summary>
public sealed class UrlLauncher(IProcessRunner processRunner)
{
    // ReSharper disable once UnusedMethodReturnValue.Global -- current callers ignore the success flag.
    public bool Open(string? url)
    {
        // Only well-formed http(s) URLs reach the desktop handler — never a raw string that
        // could be coerced into a local path or a command.
        var normalizedUrl = NormalizeHttpUrl(url);
        if (normalizedUrl is null)
        {
            return false;
        }

        var uri = new Uri(normalizedUrl, UriKind.Absolute);
        var result = processRunner.LaunchUri(uri);
        if (!result.Started)
        {
            Trace.WriteLine(
                $"[UrlLauncher] Failed to open {uri.AbsoluteUri}: {result.StartError}"
            );
        }

        return result.Started;
    }

    /// <summary>Returns a canonical absolute HTTP(S) URL, or null when the value is unsafe.</summary>
    internal static string? NormalizeHttpUrl(string? url)
    {
        if (
            !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        )
        {
            return null;
        }

        return uri.AbsoluteUri;
    }
}
