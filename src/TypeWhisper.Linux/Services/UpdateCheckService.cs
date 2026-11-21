using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Outcome of an update check. <see cref="Checked" /> distinguishes "never
///     run" from "ran"; <see cref="Faulted" /> marks a check that ran but failed
///     (offline, rate-limited, no releases yet). When a check succeeds and a
///     newer release exists, <see cref="UpdateAvailable" /> is true and
///     <see cref="LatestVersion" />/<see cref="ReleaseUrl" /> are populated.
/// </summary>
public sealed record UpdateCheckResult
{
    public bool Checked { get; init; }
    public bool Faulted { get; init; }
    public bool UpdateAvailable { get; init; }
    public string? CurrentVersion { get; init; }
    public string? LatestVersion { get; init; }
    public string? ReleaseUrl { get; init; }
    // ReSharper disable once UnusedAutoPropertyAccessor.Global  part of the UpdateCheckResult result-record data shape (set when a check faults; carried for callers/diagnostics)
    public string? Error { get; init; }

    public static UpdateCheckResult NotChecked { get; } = new();
}

/// <summary>
///     Checks the project's GitHub Releases for a newer version. Backs the
///     About section's "Check for Updates" button and a daily background check
///     that drives a non-obtrusive banner in the main window.
/// </summary>
public sealed class UpdateCheckService
{
    // List all releases and pick the highest SemVer ourselves: /releases/latest
    // sorts by publish time so a re-published older tag can shadow a newer one.
    private const string ReleasesApi =
        "https://api.github.com/repos/csmashe/typewhisper-linux/releases?per_page=100";

    // Fallback link if the API response omits html_url for some reason.
    private const string ReleasesPage =
        "https://github.com/csmashe/typewhisper-linux/releases/latest";

    private static readonly TimeSpan s_checkInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan s_failedCheckRetry = TimeSpan.FromHours(1);
    private static readonly TimeSpan s_recordingDeferral = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan s_minimumWait = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan s_snoozeDuration = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly HttpClient _httpClient;
    private readonly LinuxPreferencesService _prefs;
    private readonly Func<DateTime> _utcNow;

    private DateTime? _lastAttemptUtc;
    private string? _notifiedVersion;

    public UpdateCheckService(LinuxPreferencesService prefs, HttpClient? httpClient = null)
        : this(prefs, httpClient, utcNow: null, delay: null)
    {
    }

    // Test seam: a settable clock and a delay the test completes by hand.
    internal UpdateCheckService(
        LinuxPreferencesService prefs,
        HttpClient? httpClient,
        Func<DateTime>? utcNow,
        Func<TimeSpan, CancellationToken, Task>? delay
    )
    {
        _prefs = prefs;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
        _delay = delay ?? Task.Delay;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // GitHub's API rejects requests without a User-Agent.
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TypeWhisper-Linux");
        }
    }

    /// <summary>The most recent check result, or <see cref="UpdateCheckResult.NotChecked" />.</summary>
    public UpdateCheckResult LastResult { get; private set; } = UpdateCheckResult.NotChecked;

    /// <summary>
    ///     Checks at startup, then daily for the app's lifetime. Waits out recordings, retries a
    ///     failed check after an hour, wakes when Later ends, and stops once the opt-out is set.
    /// </summary>
    public async Task RunBackgroundChecksAsync(
        Func<bool> isRecording,
        CancellationToken cancellationToken = default
    )
    {
        while (_prefs.Current.CheckForUpdatesOnStartup)
        {
            if (isRecording())
            {
                await _delay(s_recordingDeferral, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await CheckIfDueAsync(cancellationToken).ConfigureAwait(false);
            RaiseNoticeIfDue();
            await _delay(TimeUntilNextWake(), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CheckIfDueAsync(CancellationToken cancellationToken)
    {
        // A failed refresh falls back to the cached release, so a known update keeps its banner.
        if (_utcNow() >= NextCheckDueUtc()
            && !(await CheckAsync(cancellationToken).ConfigureAwait(false)).Faulted)
        {
            return;
        }

        var known = _prefs.Current.LastKnownLatestVersion;
        if (string.IsNullOrWhiteSpace(known))
        {
            return;
        }

        // Re-surface the cached result so listeners recompute the banner (e.g. a snooze ended).
        var current = AppVersion.Display;
        Publish(
            new UpdateCheckResult
            {
                Checked = true,
                UpdateAvailable = AppVersion.Compare(current, known) < 0,
                CurrentVersion = current,
                LatestVersion = known,
                ReleaseUrl = string.IsNullOrWhiteSpace(_prefs.Current.LastKnownLatestUrl)
                    ? ReleasesPage
                    : _prefs.Current.LastKnownLatestUrl,
            }
        );
    }

    private DateTime NextCheckDueUtc()
    {
        // A stamp from a clock that later went backwards counts as due.
        var next = _prefs.Current.LastUpdateCheckUtc is { } last && last <= _utcNow()
            ? last + s_checkInterval
            : DateTime.MinValue;

        // A failed check doesn't move LastUpdateCheckUtc, so back off from the attempt instead.
        if (_lastAttemptUtc is { } attempt && attempt + s_failedCheckRetry > next)
        {
            next = attempt + s_failedCheckRetry;
        }

        return next;
    }

    private TimeSpan TimeUntilNextWake()
    {
        var now = _utcNow();
        var wake = NextCheckDueUtc();
        if (_prefs.Current.UpdateSnoozedUntilUtc is { } snoozedUntil && snoozedUntil > now && snoozedUntil < wake)
        {
            wake = snoozedUntil;
        }

        var wait = wake - now;
        return wait < s_minimumWait ? s_minimumWait : wait;
    }

    // Announces each new version once, unless the user already skipped it or said Later.
    private void RaiseNoticeIfDue()
    {
        if (LastResult is not { Checked: true, Faulted: false, UpdateAvailable: true, LatestVersion: { } version }
            || IsSuppressed(version)
            || string.Equals(_notifiedVersion ?? _prefs.Current.LastNotifiedUpdateVersion, version, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Kept in memory too, so a preferences write failure can't turn this into a daily repeat.
        _notifiedVersion = version;
        try
        {
            _prefs.Update(current => current with { LastNotifiedUpdateVersion = version });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[UpdateCheck] Could not persist the notified version: {ex.Message}");
        }

        UpdateNoticeDue?.Invoke(LastResult);
    }

    /// <summary>Live GitHub check. Used by the manual button and the background loop. Serialized against races.</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lastAttemptUtc = _utcNow();
            var current = AppVersion.Display;
            UpdateCheckResult result;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");

                using var response = await _httpClient
                    .SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var stream = await response
                    .Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                var releases = await JsonSerializer
                    .DeserializeAsync<List<GitHubRelease>>(stream, s_jsonOptions, cancellationToken)
                    .ConfigureAwait(false);

                var latest = SelectHighestStable(releases, out var latestUrl);
                if (string.IsNullOrWhiteSpace(latest))
                {
                    result = new UpdateCheckResult
                    {
                        Checked = true,
                        Faulted = true,
                        CurrentVersion = current,
                        Error = "No published release was found.",
                    };
                }
                else
                {
                    result = new UpdateCheckResult
                    {
                        Checked = true,
                        UpdateAvailable = AppVersion.Compare(current, latest) < 0,
                        CurrentVersion = current,
                        LatestVersion = latest,
                        ReleaseUrl = string.IsNullOrWhiteSpace(latestUrl) ? ReleasesPage : latestUrl,
                    };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller cancellation is not a fault — propagate. HttpClient timeouts
                // also throw OCE but with the token not requested, so they fall through.
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateCheckService] Check failed: {ex.Message}");
                result = new UpdateCheckResult
                {
                    Checked = true, Faulted = true, CurrentVersion = current, Error = ex.Message,
                };
            }

            // Only persist on a clean check — transient failures must not reset
            // the rate-limit clock or wipe the cached latest version.
            if (!result.Faulted)
            {
                try
                {
                    _prefs.Update(
                        preferences =>
                            preferences with
                            {
                                LastUpdateCheckUtc = _utcNow(),
                                LastKnownLatestVersion = result.LatestVersion,
                                LastKnownLatestUrl = result.ReleaseUrl,
                            }
                    );
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A successful check still publishes its result; only the rate-limit
                    // bookkeeping is lost when preferences can't be written.
                    Trace.WriteLine(
                        $"[UpdateCheck] Could not persist the check timestamp: {ex.Message}"
                    );
                }
            }

            Publish(result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Hides the banner for this version until a newer one ships.</summary>
    public void DismissUpdate(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        try
        {
            _prefs.Update(current => current with { DismissedUpdateVersion = version });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Dismissal is a UI command; a write failure must not tear down the banner path.
            Trace.WriteLine($"[UpdateCheck] Could not persist the dismissal: {ex.Message}");
            return;
        }

        // Re-raise so banner listeners recompute visibility.
        ResultChanged?.Invoke(LastResult);
    }

    /// <summary>"Later": hides the banner for <see cref="s_snoozeDuration" />, whatever version is out.</summary>
    public void SnoozeUpdate()
    {
        try
        {
            _prefs.Update(current => current with { UpdateSnoozedUntilUtc = _utcNow() + s_snoozeDuration });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[UpdateCheck] Could not persist the snooze: {ex.Message}");
            return;
        }

        ResultChanged?.Invoke(LastResult);
    }

    /// <summary>True when the banner must stay hidden for <paramref name="version" />: skipped, or snoozed with Later.</summary>
    public bool IsSuppressed(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var prefs = _prefs.Current;
        return string.Equals(prefs.DismissedUpdateVersion, version, StringComparison.OrdinalIgnoreCase)
               || prefs.UpdateSnoozedUntilUtc > _utcNow();
    }

    /// <summary>Raised (possibly off the UI thread) whenever <see cref="LastResult" /> changes.</summary>
    public event Action<UpdateCheckResult>? ResultChanged;

    /// <summary>Raised (off the UI thread) the first time the background check finds a version the user hasn't been told about.</summary>
    public event Action<UpdateCheckResult>? UpdateNoticeDue;

    private void Publish(UpdateCheckResult result)
    {
        LastResult = result;
        ResultChanged?.Invoke(result);
    }

    /// <summary>Returns the highest-versioned published non-prerelease/non-draft release, or null.</summary>
    private static string? SelectHighestStable(
        IReadOnlyList<GitHubRelease>? releases,
        out string? htmlUrl
    )
    {
        htmlUrl = null;
        if (releases is null)
        {
            return null;
        }

        string? best = null;
        foreach (var release in releases)
        {
            if (release.Draft || release.Prerelease)
            {
                continue;
            }

            var version = NormalizeTag(release.TagName);
            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            if (best is not null && AppVersion.Compare(version, best) <= 0)
            {
                continue;
            }

            best = version;
            htmlUrl = release.HtmlUrl;
        }

        return best;
    }

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var trimmed = tag.Trim();
        return trimmed.StartsWith('v') || trimmed.StartsWith('V') ? trimmed[1..] : trimmed;
    }

    // Populated by System.Text.Json deserialization (reflection); the init accessors are written
    // by the deserializer, which ReSharper cannot see.
    // ReSharper disable UnusedAutoPropertyAccessor.Local
    // ReSharper disable once ClassNeverInstantiated.Local -- instantiated by System.Text.Json deserialization, which ReSharper cannot see.
    private sealed record GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }
    }
    // ReSharper restore UnusedAutoPropertyAccessor.Local
}
