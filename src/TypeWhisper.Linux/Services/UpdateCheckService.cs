using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Outcome of an update check. <see cref="Checked"/> distinguishes "never
///     run" from "ran"; <see cref="Faulted"/> marks a check that ran but failed
///     (offline, rate-limited, no releases yet). When a check succeeds and a
///     newer release exists, <see cref="UpdateAvailable"/> is true and
///     <see cref="LatestVersion"/>/<see cref="ReleaseUrl"/> are populated.
/// </summary>
public sealed record UpdateCheckResult
{
    public bool Checked { get; init; }
    public bool Faulted { get; init; }
    public bool UpdateAvailable { get; init; }
    public string? CurrentVersion { get; init; }
    public string? LatestVersion { get; init; }
    public string? ReleaseUrl { get; init; }
    public string? Error { get; init; }

    public static UpdateCheckResult NotChecked { get; } = new();
}

/// <summary>
///     Checks the project's GitHub Releases for a newer version. Backs the
///     About section's "Check for Updates" button and a once-per-day startup
///     check that drives a non-obtrusive banner in the main window.
/// </summary>
public sealed class UpdateCheckService
{
    // We list all releases and pick the highest SemVer ourselves rather than
    // trusting GitHub's /releases/latest endpoint: that endpoint sorts by
    // publish time, not version, so a re-published older tag can shadow a
    // newer one. per_page=100 covers far more releases than this fork will
    // ever have in flight.
    private const string ReleasesApi =
        "https://api.github.com/repos/csmashe/typewhisper-linux/releases?per_page=100";

    // Fallback link if the API response omits html_url for some reason.
    private const string ReleasesPage =
        "https://github.com/csmashe/typewhisper-linux/releases/latest";

    private static readonly TimeSpan s_startupCheckInterval = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly LinuxPreferencesService _prefs;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpdateCheckService(LinuxPreferencesService prefs, HttpClient? httpClient = null)
    {
        _prefs = prefs;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // GitHub's API rejects requests without a User-Agent.
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TypeWhisper-Linux");
        }
    }

    /// <summary>The most recent check result, or <see cref="UpdateCheckResult.NotChecked"/>.</summary>
    public UpdateCheckResult LastResult { get; private set; } = UpdateCheckResult.NotChecked;

    /// <summary>Raised (possibly off the UI thread) whenever <see cref="LastResult"/> changes.</summary>
    public event Action<UpdateCheckResult>? ResultChanged;

    /// <summary>
    ///     Called once at startup. Honors the user's opt-out, rate-limits the
    ///     network call to once per day, and — when a check isn't due — still
    ///     re-surfaces the last known result so the banner reappears without
    ///     hitting the network.
    /// </summary>
    public async Task CheckOnStartupAsync(CancellationToken cancellationToken = default)
    {
        if (!_prefs.Current.CheckForUpdatesOnStartup)
        {
            return;
        }

        var last = _prefs.Current.LastUpdateCheckUtc;
        var due = last is not { } stamp || DateTime.UtcNow - stamp >= s_startupCheckInterval;

        if (due)
        {
            await CheckAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // Not due — recompute from the cached latest version, no network.
        var known = _prefs.Current.LastKnownLatestVersion;
        if (string.IsNullOrWhiteSpace(known))
        {
            return;
        }

        var current = AppVersion.Display;
        Publish(
            new UpdateCheckResult
            {
                Checked = true,
                UpdateAvailable = AppVersion.Compare(current, known) < 0,
                CurrentVersion = current,
                LatestVersion = known,
                // Reuse the cached release URL; only fall back to the generic
                // releases page if we somehow have a version but no URL.
                ReleaseUrl = string.IsNullOrWhiteSpace(_prefs.Current.LastKnownLatestUrl)
                    ? ReleasesPage
                    : _prefs.Current.LastKnownLatestUrl
            }
        );
    }

    /// <summary>
    ///     Performs a live check against GitHub. Used by the manual button and
    ///     the due startup path. Serialized so overlapping calls can't race.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
                        Error = "No published release was found."
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
                        ReleaseUrl = string.IsNullOrWhiteSpace(latestUrl) ? ReleasesPage : latestUrl
                    };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller-requested cancellation is not a fault; let it propagate.
                // (HttpClient timeouts also throw here but with the token not
                // requested, so they still fall through to the fault path below.)
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateCheckService] Check failed: {ex.Message}");
                result = new UpdateCheckResult
                {
                    Checked = true,
                    Faulted = true,
                    CurrentVersion = current,
                    Error = ex.Message
                };
            }

            // Only persist on a clean check so a transient failure can't reset
            // the rate-limit clock or wipe the cached latest version.
            if (!result.Faulted)
            {
                _prefs.Save(
                    _prefs.Current with
                    {
                        LastUpdateCheckUtc = DateTime.UtcNow,
                        LastKnownLatestVersion = result.LatestVersion,
                        LastKnownLatestUrl = result.ReleaseUrl
                    }
                );
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

        _prefs.Save(_prefs.Current with { DismissedUpdateVersion = version });

        // Re-raise so banner listeners recompute visibility against the new
        // dismissed-version preference.
        ResultChanged?.Invoke(LastResult);
    }

    /// <summary>True when <paramref name="version"/> was dismissed from the banner.</summary>
    public bool IsDismissed(string? version)
    {
        return !string.IsNullOrWhiteSpace(version)
            && string.Equals(
                _prefs.Current.DismissedUpdateVersion,
                version,
                StringComparison.OrdinalIgnoreCase
            );
    }

    private void Publish(UpdateCheckResult result)
    {
        LastResult = result;
        ResultChanged?.Invoke(result);
    }

    /// <summary>
    ///     Picks the highest-versioned published, non-prerelease, non-draft
    ///     release from the list and returns its normalized version (and html
    ///     URL). Null when the list has no qualifying release.
    /// </summary>
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

            if (best is null || AppVersion.Compare(version, best) > 0)
            {
                best = version;
                htmlUrl = release.HtmlUrl;
            }
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
}
