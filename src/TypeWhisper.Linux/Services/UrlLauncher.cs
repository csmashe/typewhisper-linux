using System.Diagnostics;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Opens a URL in the user's default browser. Uses UseShellExecute, which
///     on Linux .NET routes through the desktop's URL handler (xdg-open).
/// </summary>
public static class UrlLauncher
{
    // ReSharper disable once UnusedMethodReturnValue.Global -- returns whether the launch succeeded for callers that want it; current callers ignore it.
    public static bool Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        // Only hand off well-formed http(s) URLs to the shell handler — never a
        // raw string that could be coerced into a local path or command.
        if (
            !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        )
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UrlLauncher] Failed to open {uri.AbsoluteUri}: {ex.Message}");
            return false;
        }
    }
}