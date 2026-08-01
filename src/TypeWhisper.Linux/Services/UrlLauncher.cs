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
        if (
            !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        )
        {
            return false;
        }

        var result = processRunner.LaunchUri(uri);
        if (!result.Started)
        {
            Debug.WriteLine(
                $"[UrlLauncher] Failed to open {uri.AbsoluteUri}: {result.StartError}"
            );
        }

        return result.Started;
    }
}
