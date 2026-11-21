using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>The active tab a TypeWhisper browser extension reported, tied to the window in front when it arrived.</summary>
/// <param name="ClientId">The reporting extension's identifier.</param>
/// <param name="ProcessName">The foreground window's process name when the report arrived.</param>
/// <param name="ProcessId">That window's PID, or null when the provider reports none.</param>
/// <param name="WindowId">That window's provider id, or null.</param>
/// <param name="Url">An http, https or file URL; null when the active tab shows no website.</param>
/// <param name="Title">The trimmed tab title, or null.</param>
/// <param name="ReceivedAt">When the report arrived.</param>
public sealed record BrowserExtensionReport(
    string ClientId,
    string ProcessName,
    int? ProcessId,
    string? WindowId,
    string? Url,
    string? Title,
    DateTimeOffset ReceivedAt
);

/// <summary>
///     Keeps the latest report from <c>PUT /v1/browser/context</c> in memory. The extension repeats it every
///     minute while its window has focus, so a report that is not repeated within
///     <see cref="ReportLifetime" /> is treated as gone.
/// </summary>
public sealed class BrowserExtensionContextStore(TimeProvider? timeProvider = null)
{
    public static TimeSpan ReportLifetime { get; } = TimeSpan.FromSeconds(180);

    private static readonly string s_ownProcessName = GetOwnProcessName();

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _lock = new();
    // Sequence of each client's latest clear, so a focused report still waiting for its foreground lookup
    // can't bring back a report its own extension has since cleared.
    private readonly Dictionary<string, long> _clearedAt = new(StringComparer.Ordinal);
    private long _nextSequence;
    private BrowserExtensionReport? _report;
    private long _reportSequence;

    /// <summary>Takes the arrival order for a report, before any wait such as the foreground lookup.</summary>
    public long BeginReport()
    {
        return Interlocked.Increment(ref _nextSequence);
    }

    /// <inheritdoc cref="Report(long, string, bool, string?, string?, ActiveWindowSnapshot?)" />
    public BrowserExtensionReport? Report(
        string clientId,
        bool focused,
        string? url,
        string? title,
        ActiveWindowSnapshot? foreground
    )
    {
        return Report(BeginReport(), clientId, focused, url, title, foreground);
    }

    /// <summary>
    ///     Records a report. A focused report replaces the previous one and belongs to
    ///     <paramref name="foreground" />; it is ignored while TypeWhisper itself is in front or the window's
    ///     owner is unknown. A report without focus removes only this client's report, so a browser losing
    ///     focus can't clear the report of the browser that took it. A report older (by
    ///     <paramref name="sequence" />) than one already applied is dropped.
    /// </summary>
    /// <returns>The stored report, or null when nothing was stored.</returns>
    public BrowserExtensionReport? Report(
        long sequence,
        string clientId,
        bool focused,
        string? url,
        string? title,
        ActiveWindowSnapshot? foreground
    )
    {
        if (!focused)
        {
            lock (_lock)
            {
                if (_clearedAt.Count >= 64 && !_clearedAt.ContainsKey(clientId))
                {
                    _clearedAt.Clear();
                }

                _clearedAt[clientId] = Math.Max(sequence, _clearedAt.GetValueOrDefault(clientId));
                if (_report?.ClientId == clientId && _reportSequence < sequence)
                {
                    _report = null;
                }
            }

            return null;
        }

        if (
            foreground is not { ProcessName: { Length: > 0 } processName, ProcessIdUnresolved: false }
            || IsOwnProcess(foreground)
        )
        {
            return null;
        }

        var report = new BrowserExtensionReport(
            clientId,
            processName,
            foreground.ProcessId,
            foreground.WindowId,
            url,
            title,
            _time.GetUtcNow()
        );
        lock (_lock)
        {
            if (sequence < _reportSequence || _clearedAt.GetValueOrDefault(clientId) > sequence)
            {
                return null;
            }

            _report = report;
            _reportSequence = sequence;
        }

        return report;
    }

    /// <summary>
    ///     The current report for <paramref name="snapshot" />, or null when none belongs to that window.
    ///     The process name must match, and the PID and window id too wherever both sides know them.
    /// </summary>
    public BrowserExtensionReport? Current(ActiveWindowSnapshot? snapshot)
    {
        if (snapshot is not { ProcessName: { Length: > 0 } processName, ProcessIdUnresolved: false })
        {
            return null;
        }

        BrowserExtensionReport? report;
        lock (_lock)
        {
            report = _report;
        }

        if (
            report is null
            || !string.Equals(report.ProcessName, processName, StringComparison.OrdinalIgnoreCase)
            || (report.ProcessId is { } reportPid && snapshot.ProcessId is { } snapshotPid && reportPid != snapshotPid)
            || (
                report.WindowId is { Length: > 0 } reportWindow
                && snapshot.WindowId is { Length: > 0 } snapshotWindow
                && !string.Equals(reportWindow, snapshotWindow, StringComparison.Ordinal)
            )
        )
        {
            return null;
        }

        return _time.GetUtcNow() - report.ReceivedAt <= ReportLifetime ? report : null;
    }

    /// <summary>
    ///     Accepts http, https and file URLs shorter than 2048 characters. New tabs and browser pages such as
    ///     chrome://settings return null, meaning no website.
    /// </summary>
    public static string? NormalizeUrl(string? value)
    {
        var text = value?.Trim();
        if (
            text is null
            || text.Length is <= 3 or >= 2048
            || text.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "file")
        )
        {
            return null;
        }

        return text;
    }

    /// <summary>Trims a tab title to at most 512 characters without splitting a surrogate pair; empty returns null.</summary>
    public static string? NormalizeTitle(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return text.Length <= 512 ? text : text[..(char.IsHighSurrogate(text[511]) ? 511 : 512)];
    }

    // Providers without a PID report only the name, so compare that too.
    private static bool IsOwnProcess(ActiveWindowSnapshot snapshot)
    {
        if (snapshot.ProcessId is { } pid)
        {
            return pid == Environment.ProcessId;
        }

        return string.Equals(snapshot.ProcessName, s_ownProcessName, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetOwnProcessName()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.ProcessName;
    }
}
