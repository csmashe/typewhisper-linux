using System.Net;

namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>Recognizes HTTP endpoints that are provably on this computer.</summary>
public static class LoopbackEndpoint
{
    /// <summary>
    ///     True for an absolute http or https URL whose host is "localhost" or a loopback IP literal.
    ///     A hostname is never resolved, since it could point elsewhere or change after the check.
    /// </summary>
    public static bool IsLoopback(Uri? endpoint)
    {
        if (endpoint is not { IsAbsoluteUri: true }
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        return endpoint.HostNameType switch
        {
            UriHostNameType.Dns => string.Equals(endpoint.IdnHost, "localhost", StringComparison.OrdinalIgnoreCase),
            UriHostNameType.IPv4 or UriHostNameType.IPv6 =>
                IPAddress.TryParse(endpoint.DnsSafeHost, out var address) && IPAddress.IsLoopback(address),
            _ => false,
        };
    }
}
