using Sentry.Protocol;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TypeWhisper.Linux.Services.Telemetry;

public static partial class TelemetryScrubber
{
    private static readonly HashSet<string> s_allowedContexts = new(StringComparer.OrdinalIgnoreCase)
    {
        "app",
        "os",
        "runtime",
        "device",
        "gpu",
        "trace",
        "Current Culture",
        "Memory Info",
        "ThreadPool Info",
        "Dynamic Code",
        ".NET",
    };


    private static string ScrubText(string? text)
    {
        return ScrubText(text,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.UserName, Environment.MachineName);
    }

    public static string ScrubText(string? text, string? homeDirectory, string? userName, string? machineName)
    {
        var value = text ?? "";
        var home = homeDirectory?.TrimEnd('/');
        if (!string.IsNullOrEmpty(home))
        {
            value = value.Replace(home, "~", StringComparison.Ordinal);
        }

        value = HomeDirectoryRegex().Replace(value, "/home/<user>");
        value = EmailRegex().Replace(value, "<email>");
        value = ReplaceWord(value, machineName, "<host>");
        value = ReplaceWord(value, userName, "<user>");
        value = IPv6Regex().Replace(value, "<ip>");
        value = IPv4Regex().Replace(value, "<ip>");
        value = AuthorizationSchemeRegex().Replace(value, "$1 <redacted>");
        value = UrlQueryRegex().Replace(value, "$1?<redacted>");
        value = KnownKeyPrefixRegex().Replace(value, "<redacted>");
        value = JsonSecretRegex().Replace(value, """
            "$1":"<redacted>"
            """);
        return KeyValueSecretRegex().Replace(value, "<redacted>");
    }

    [GeneratedRegex("""/home/[^/\s'"<>]+""")]
    private static partial Regex HomeDirectoryRegex();

    [GeneratedRegex(@"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex IPv4Regex();

    // Full, compressed (::) and IPv4-mapped literals; the digit lookahead keeps Namespace::Type and
    // hex-only identifiers out, the missing :: keeps hh:mm:ss timestamps out.
    [GeneratedRegex(
        @"(?<![\w:])(?=[0-9a-f:.]*\d)(?:(?:[0-9a-f]{1,4}:){7}[0-9a-f]{1,4}"
        + @"|(?:[0-9a-f]{1,4}:){1,7}:(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){0,6})?"
        + @"|::(?:ffff:)?(?:\d{1,3}\.){3}\d{1,3}"
        + @"|::[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){0,6})(?![\w:])",
        RegexOptions.IgnoreCase)]
    private static partial Regex IPv6Regex();

    [GeneratedRegex("""\b(Bearer|Basic)\s+[^\s'"]+""", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationSchemeRegex();

    [GeneratedRegex("""([a-z][a-z0-9+.-]*://[^\s'"?]+)\?[^\s'"]*""", RegexOptions.IgnoreCase)]
    private static partial Regex UrlQueryRegex();

    [GeneratedRegex("sk-[A-Za-z0-9_-]{16,}|gsk_[A-Za-z0-9]{16,}|AIza[0-9A-Za-z_-]{20,}|xai-[A-Za-z0-9]{16,}")]
    private static partial Regex KnownKeyPrefixRegex();

    [GeneratedRegex("""
        "(api[_-]?key|token|secret|password|authorization|access[_-]?token|refresh[_-]?token)"\s*:\s*"[^"]*"
        """, RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecretRegex();

    [GeneratedRegex(@"\b(api[_-]?key|token|secret|password)\s*[=:]\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueSecretRegex();

    private static string ReplaceWord(string text, string? word, string replacement)
    {
        return word is { Length: >= 3 } ? Regex.Replace(text, @"\b" + Regex.Escape(word) + @"\b", replacement) : text;
    }

    public static SentryEvent? Scrub(SentryEvent? e) => Scrub(e, ScrubText);
    public static SentryTransaction? Scrub(SentryTransaction? t) => Scrub(t, ScrubText);
    public static Breadcrumb? Scrub(Breadcrumb? b) => Scrub(b, ScrubText);

    internal static Breadcrumb? ScrubExceptionBreadcrumb(Breadcrumb? breadcrumb, SentryHint hint)
    {
        if (hint.Items.TryGetValue("exception", out var value)
            && value is Exception)
        {
            return Scrub(breadcrumb, _ => "<redacted>");
        }

        return Scrub(breadcrumb);
    }


    internal static SentryEvent? Scrub(SentryEvent? e, Func<string?, string> scrub)
    {
        if (e is null)
        {
            return null;
        }

        e.ServerName = null;
        ScrubCommon(e, scrub);
        if (e.Message is { } message)
        {
            e.Message = new SentryMessage
            {
                Message = message.Message is null ? null : scrub(message.Message),
                Formatted = message.Formatted is null ? null : scrub(message.Formatted),
                Params = message.Params?.Select(p => p is string s ? scrub(s) : p).ToArray(),
            };
        }

        if (e.Exception is not null)
        {
            TelemetryErrorFacts.Apply(e);
        }

        foreach (var exception in e.SentryExceptions ?? [])
        {
            exception.Value = "<redacted>";
            // Exception.Data is copied into mechanism.data by the SDK; it is free-form caller content.
            if (exception.Mechanism is { } mechanism)
            {
                mechanism.Data.Clear();
                mechanism.Meta.Clear();
            }

            ScrubStack(exception.Stacktrace, scrub);
        }

        foreach (var thread in e.SentryThreads ?? [])
        {
            ScrubStack(thread.Stacktrace, scrub);
        }

        foreach (var image in e.DebugImages ?? [])
        {
            image.CodeFile = image.CodeFile is null ? null : scrub(image.CodeFile);
            image.DebugFile = image.DebugFile is null ? null : scrub(image.DebugFile);
        }

        return e;
    }

    // ReSharper disable once MemberCanBePrivate.Global -- used by tests via InternalsVisibleTo
    internal static SentryTransaction? Scrub(SentryTransaction? t, Func<string?, string> scrub)
    {
        if (t is null)
        {
            return null;
        }

        ScrubCommon(t, scrub);
        t.Description = t.Description is null ? null : scrub(t.Description);
        foreach (var span in t.Spans)
        {
            span.Description = span.Description is null ? null : scrub(span.Description);
            foreach (var data in span.Data.ToArray())
            {
                if (data.Value is string value)
                {
                    span.SetData(data.Key, scrub(value));
                }
            }

            foreach (var tag in span.Tags.ToArray())
            {
                span.SetTag(tag.Key, scrub(tag.Value));
            }
        }

        return t;
    }

    private static Breadcrumb? Scrub(Breadcrumb? b, Func<string?, string> scrub)
    {
        return b is null ? null : Breadcrumb.FromJson(JsonSerializer.SerializeToElement(new
        {
            timestamp = b.Timestamp,
            message = b.Message is null ? null : scrub(b.Message),
            type = b.Type,
            data = b.Data?.ToDictionary(pair => pair.Key, pair => scrub(pair.Value)),
            category = b.Category,
            level = b.Level.ToString().ToLowerInvariant(),
        }));
    }

    private static void ScrubCommon(IEventLike e, Func<string?, string> scrub)
    {
        e.User = new SentryUser();
        e.Request = new SentryRequest();
        ((IDictionary<string, object?>)e.Extra).Clear();
        foreach (var key in e.Contexts.Keys.ToArray())
        {
            if (!s_allowedContexts.Contains(key))
            {
                e.Contexts.Remove(key);
            }
        }

        foreach (var device in e.Contexts.Values.OfType<Device>())
        {
            device.Name = null;
            device.DeviceUniqueIdentifier = null;
            device.Timezone = null;
            device.BootTime = null;
        }

        foreach (var app in e.Contexts.Values.OfType<Sentry.Protocol.App>())
        {
            app.Hash = null;
            app.Identifier = null;
        }

        foreach (var tag in e.Tags.ToArray())
        {
            e.SetTag(tag.Key, scrub(tag.Value));
        }

        if (e.TransactionName is not null)
        {
            e.TransactionName = scrub(e.TransactionName);
        }

        var breadcrumbs = (IList<Breadcrumb>)e.Breadcrumbs;
        for (var i = 0; i < breadcrumbs.Count; i++)
        {
            breadcrumbs[i] = Scrub(breadcrumbs[i], scrub)!;
        }
    }

    private static void ScrubStack(SentryStackTrace? stack, Func<string?, string> scrub)
    {
        foreach (var frame in stack?.Frames ?? [])
        {
            frame.FileName = frame.FileName is null ? null : scrub(frame.FileName);
            frame.AbsolutePath = frame.AbsolutePath is null ? null : scrub(frame.AbsolutePath);
            frame.Module = frame.Module is null ? null : scrub(frame.Module);
            frame.Package = frame.Package is null ? null : scrub(frame.Package);
        }
    }
}
