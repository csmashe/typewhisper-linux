using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.ManagedArtifacts;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     One-click installer for the browser accessibility surface that powers AT-SPI URL detection.
///     Mirrors <see cref="Insertion.YdotoolSetupHelper" /> in shape. Two artifacts are installed:
///     1. <c>~/.config/environment.d/typewhisper-accessibility.conf</c> — exports
///        <c>MOZ_ENABLE_ACCESSIBILITY=1</c> and <c>GTK_MODULES=gail:atk-bridge</c>.
///     2. User-local <c>.desktop</c> overrides in <c>~/.local/share/applications/</c> — shadows
///        the system launcher to add <c>--force-renderer-accessibility</c> (Chromium) or the env
///        wrapper (Firefox) without modifying system files.
///     Both artifacts carry <see cref="OwnershipMarker" /> so <see cref="RemoveAsync" /> can
///     confirm ownership before deletion.
/// </summary>
public sealed partial class BrowserAccessibilitySetupHelper
{
    private const UnixFileMode PrivateConfigMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode DesktopFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite
        | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    internal static string? ManagedArtifactStateRootOverride { get; set; }
    internal static IReadOnlyList<string>? SystemLauncherDirectoriesOverride { get; set; }
    internal static IReadOnlyList<string>? FirefoxProfileRootsOverride { get; set; }
    private const string OwnershipMarker = "Installed by TypeWhisper";

    private const string EnvFileName = "typewhisper-accessibility.conf";

    private const string EnvFileContent =
        "# "
        + OwnershipMarker
        + " — enables Firefox / GTK accessibility so\n"
        + "# the dictation overlay can read your browser URL for profile\n"
        + "# matching. Remove this file (and rerun) to roll back.\n"
        + "MOZ_ENABLE_ACCESSIBILITY=1\n"
        + "GTK_MODULES=gail:atk-bridge\n";

    private const string DesktopOwnershipComment =
        "# " + OwnershipMarker + " - patches Exec= for URL detection";

    private const string FirefoxEnvWrapper =
        "env MOZ_ENABLE_ACCESSIBILITY=1 GTK_MODULES=gail:atk-bridge";

    private const string UserJsOwnershipMarker = "// Set by TypeWhisper";

    private const string UserJsPreservedPrefix = "// TypeWhisper preserved: ";

    private const string UserJsOwnedSeparatorSuffix = "; separator newline owned";

    // Launcher names and Firefox profile roots now come from BrowserDescriptorCatalog; only the
    // export roots stay here.
    //
    // Appended even when XDG_DATA_DIRS omits them: Flatpak's profile.d snippet and
    // systemd generator do not reach every session type, so an absent export root means
    // a propagation gap. System roots get no such treatment — one the session left out
    // is one whose launchers the desktop does not read at all.
    private static readonly string[] s_flatpakExportRoots =
    [
        "/var/lib/flatpak/exports/share",
    ];

    /// <summary>
    ///     True only on Wayland sessions, where the AT-SPI walker is the
    ///     only way to capture a browser's current URL. On X11 the existing
    ///     xdotool + xclip Ctrl+L/Ctrl+C path already covers URL capture,
    ///     so prompting the user to enable browser accessibility there
    ///     would just be noise.
    /// </summary>
    public static bool IsApplicable()
    {
        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        return string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<string> GetLauncherNames(
        BrowserLauncherPatchMode patchMode
    )
    {
        return BrowserDescriptorCatalog.GetDesktopIds(patchMode);
    }

    internal static IReadOnlyList<string> GetFirefoxProfileRoots()
    {
        return BrowserDescriptorCatalog.GetExpandedProfileRoots();
    }

    public static Status IsCurrentlyConfigured()
    {
        // Corrupt, incomplete, or unreadable managed state must not escape a status
        // probe: this feeds Profiles view-model getters and refresh logic, where a
        // throw breaks rendering instead of just reporting "not configured".
        ManagedFileClassification envClassification;
        try
        {
            envClassification = CreateManagedTransaction().Probe(BuildEnvFileSpec());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.WriteLine(
                $"[BrowserAccessibilitySetupHelper] env-file probe failed: {ex.Message}"
            );
            envClassification = ManagedFileClassification.Foreign;
        }

        var envFilePresent = envClassification is ManagedFileClassification.CurrentOwned
            or ManagedFileClassification.StaleOwned;
        // "LauncherPresent" means every installed launcher in this family is patched, not just
        // one — a newly-installed browser (e.g. Brave after Chrome was already patched) must not
        // silently count as configured while its own launcher still lacks the flag.
        var firefoxLauncherNames = GetLauncherNames(
            BrowserLauncherPatchMode.FirefoxEnvironment
        );
        var chromiumLauncherNames = GetLauncherNames(
            BrowserLauncherPatchMode.ChromiumRendererAccessibility
        );
        var firefoxLauncherPresent = AllInstalledLaunchersOwned(
            firefoxLauncherNames,
            PrependEnvWrapperToExecLines
        );
        var chromiumLauncherPresent = AllInstalledLaunchersOwned(
            chromiumLauncherNames,
            AddAccessibilityFlagToExecLines
        );
        var firefoxInstalled = HasInstalledLauncher(firefoxLauncherNames);
        var chromiumInstalled = HasInstalledLauncher(chromiumLauncherNames);
        var firefoxProfiles = EnumerateFirefoxProfileDirs().ToList();
        var firefoxProfileFound = firefoxProfiles.Count > 0;
        // ALL profiles need the override — setup writes to every profile, so detection
        // must require every profile to pass; partial coverage would silently hide the
        // Enable button while other profiles still fail URL detection.
        var firefoxForceEnabled =
            firefoxProfileFound && firefoxProfiles.All(IsForceEnabledInProfile);
        return new Status(
            envFilePresent,
            firefoxLauncherPresent,
            chromiumLauncherPresent,
            firefoxInstalled,
            chromiumInstalled,
            firefoxForceEnabled,
            firefoxProfileFound
        );
    }

    /// <summary>
    ///     Writes <c>user_pref("accessibility.force_disabled", -1);</c> to
    ///     every discoverable Firefox profile's <c>user.js</c>. Firefox
    ///     reads <c>user.js</c> on every startup and uses it as the
    ///     authoritative override for <c>prefs.js</c>, so this is the safe
    ///     way to script-apply a pref — Firefox itself never writes back to
    ///     <c>user.js</c>, and a running Firefox won't clobber our change on
    ///     its next save. Takes effect on the next Firefox restart.
    /// </summary>
    private static SetupResult ForceEnableFirefoxAccessibility()
    {
        var patched = new List<string>();
        var skipped = new List<string>();

        try
        {
            foreach (var profileDir in EnumerateFirefoxProfileDirs())
            {
                var userJsPath = Path.Join(profileDir, "user.js");
                try
                {
                    PatchFirefoxUserJs(userJsPath);
                    patched.Add(Path.GetFileName(profileDir));
                }
                catch
                {
                    skipped.Add(Path.GetFileName(profileDir));
                }
            }

            if (patched.Count == 0 && skipped.Count == 0)
            {
                return new SetupResult(
                    false,
                    "No Firefox profiles were found to patch.",
                    "Run Firefox once to create a profile, then try again."
                );
            }

            var detail = new StringBuilder();
            if (patched.Count > 0)
            {
                detail
                    .Append("Patched profile(s): ")
                    .Append(string.Join(", ", patched))
                    .Append('.');
            }

            if (skipped.Count > 0)
            {
                if (detail.Length > 0)
                {
                    detail.Append(' ');
                }

                detail
                    .Append("Could not write to: ")
                    .Append(string.Join(", ", skipped))
                    .Append('.');
            }

            detail.Append(" Restart Firefox for the change to take effect.");

            return new SetupResult(
                patched.Count > 0,
                patched.Count > 0
                    ? "Firefox accessibility force-enabled."
                    : "Could not enable Firefox accessibility.",
                detail.ToString()
            );
        }
        catch (Exception ex)
        {
            return new SetupResult(false, "Could not enable Firefox accessibility.", ex.Message);
        }
    }

    // kept instance: invoked on the injected _browserSetup seam by callers (static would orphan the DI field)
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "kept instance: injected as a DI/test seam")]
    // ReSharper disable once MemberCanBeMadeStatic.Global
    // ReSharper disable once UnusedParameter.Global -- part of the setup API contract (cancellation support and signature symmetry with RemoveAsync)
    public Task<SetupResult> SetUpAsync(CancellationToken ct)
    {
        try
        {
            var envResult = WriteEnvFile();
            if (!envResult.OwnsDestination)
            {
                return Task.FromResult(
                    new SetupResult(
                        false,
                        "Could not enable browser accessibility.",
                        $"{BuildEnvFileSpec().DestinationPath} was left untouched because "
                        + (envResult.Detail ?? "it is not owned by TypeWhisper.")
                    )
                );
            }
            var chromiumPatched = PatchLaunchers(
                GetLauncherNames(BrowserLauncherPatchMode.ChromiumRendererAccessibility),
                AddAccessibilityFlagToExecLines
            );
            var firefoxPatched = PatchLaunchers(
                GetLauncherNames(BrowserLauncherPatchMode.FirefoxEnvironment),
                PrependEnvWrapperToExecLines
            );
            var firefoxPrefResult = ForceEnableFirefoxAccessibility();

            var detail = new StringBuilder();
            if (firefoxPatched.Count > 0)
            {
                detail.Append("Firefox / Zen launchers patched: ");
                detail.Append(string.Join(", ", firefoxPatched));
                detail.Append('.');
            }

            if (chromiumPatched.Count > 0)
            {
                if (detail.Length > 0)
                {
                    detail.Append('\n');
                }

                detail.Append("Chromium launchers patched: ");
                detail.Append(string.Join(", ", chromiumPatched));
                detail.Append('.');
            }

            if (firefoxPrefResult.Success && !string.IsNullOrWhiteSpace(firefoxPrefResult.Detail))
            {
                if (detail.Length > 0)
                {
                    detail.Append('\n');
                }

                detail.Append("Firefox accessibility: ").Append(firefoxPrefResult.Detail);
            }

            if (firefoxPatched.Count == 0 && chromiumPatched.Count == 0)
            {
                detail.Append(
                    "No browser launchers were found on this system; only the user-wide env file was written."
                );
            }
            else
            {
                detail.Append('\n');
                detail.Append(
                    "Fully quit the affected browsers and relaunch from the application menu — running instances are not retroactively patched."
                );
            }

            var success =
                firefoxPrefResult.Success
                || firefoxPatched.Count > 0
                || chromiumPatched.Count > 0
                || IsCurrentlyConfigured().IsFullyConfigured;

            return Task.FromResult(
                new SetupResult(
                    success,
                    success
                        ? "Browser accessibility enabled."
                        : "Could not enable browser accessibility.",
                    detail.ToString()
                )
            );
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                new SetupResult(false, "Could not enable browser accessibility.", ex.Message)
            );
        }
    }

    /// <summary>
    ///     Returns a human-readable list of the changes <see cref="SetUpAsync" />
    ///     would actually make right now. The Profiles UI shows this in the
    ///     confirmation dialog so the user can see what's about to be touched
    ///     on disk — file paths included — before approving. Items already
    ///     done in a prior run are omitted from the list so the dialog never
    ///     over-claims what it's doing.
    /// </summary>
    // kept instance: invoked on the injected _browserSetup seam by callers (static would orphan the DI field)
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "kept instance: injected as a DI/test seam")]
    public IReadOnlyList<string> DescribePendingActions()
    {
        var status = IsCurrentlyConfigured();
        var actions = new List<string>();

        if (!status.FirefoxEnvFilePresent)
        {
            actions.Add(
                $"• Write {EnvFilePath()}\n"
                + "  Sets MOZ_ENABLE_ACCESSIBILITY=1 and GTK_MODULES=gail:atk-bridge user-wide."
            );
        }

        if (status is { FirefoxInstalled: true, FirefoxLauncherPresent: false })
        {
            actions.Add(
                $"• Shadow Firefox / Zen .desktop launchers in {UserApplicationsDir()}\n"
                + "  Adds env MOZ_ENABLE_ACCESSIBILITY=1 to the Exec= line so the env arrives even\n"
                + "  if systemd-user did not reload environment.d across a logout."
            );
        }

        if (status is { ChromiumInstalled: true, ChromiumLauncherPresent: false })
        {
            actions.Add(
                $"• Shadow Chromium-family .desktop launchers in {UserApplicationsDir()}\n"
                + "  Adds the --force-renderer-accessibility flag to Exec=."
            );
        }

        if (
            status is not { FirefoxInstalled: true, FirefoxProfileFound: true, FirefoxAccessibilityForceEnabled: false }
        )
        {
            return actions;
        }

        var profiles = EnumerateFirefoxProfileDirs().Select(d => Path.Join(d, "user.js"));
        actions.Add(
            "• Write user.js in your Firefox profile(s) to force-enable accessibility:\n"
            + string.Join("\n", profiles.Select(p => "    " + p))
            + "\n"
            + "  Appends user_pref(\"accessibility.force_disabled\", -1); if that preference already\n"
            + "  exists, its line is commented out and preserved, then restored on removal."
        );

        return actions;
    }

    // ReSharper disable once UnusedParameter.Global -- part of the setup API contract (cancellation support and signature symmetry with SetUpAsync)
    public static async Task<SetupResult> RemoveAsync(CancellationToken ct)
    {
        try
        {
            var envRemoval = await CreateManagedTransaction()
                .RemoveAsync(BuildEnvFileSpec(), ct)
                .ConfigureAwait(false);
            var removedEnv = envRemoval.Classification == ManagedFileClassification.Absent
                || envRemoval.Changed;
            SweepLegacyEnvFile(ct);
            var removedLaunchers = await RemoveOwnedLaunchers(ct).ConfigureAwait(false);
            var cleanedProfiles = RemoveOwnedFirefoxAccessibilityEntries();

            var summary = new StringBuilder("Browser accessibility integration removed.");
            if (!removedEnv)
            {
                summary.Append(" Left env file in place (not owned by TypeWhisper).");
            }

            if (removedLaunchers.Count > 0)
            {
                summary.Append(' ');
                summary.Append("Removed launchers: ");
                summary.Append(string.Join(", ", removedLaunchers));
                summary.Append('.');
            }

            if (cleanedProfiles.Count <= 0)
            {
                return new SetupResult(true, summary.ToString());
            }

            summary.Append(' ');
            summary.Append("Cleaned Firefox profile(s): ");
            summary.Append(string.Join(", ", cleanedProfiles));
            summary.Append('.');

            return new SetupResult(true, summary.ToString());
        }
        catch (Exception ex)
        {
            return new SetupResult(
                false,
                "Could not remove browser accessibility integration.",
                ex.Message
            );
        }
    }

    /// <summary>
    ///     True when there is at least one piece of integration we installed —
    ///     env file, patched launcher, or Firefox user.js entry we own. Drives
    ///     whether the Profiles UI shows a Revert button. We never count
    ///     Firefox prefs.js entries here: those might have been set by the
    ///     user via about:config and aren't ours to remove.
    /// </summary>
    public static bool HasInstalledChanges()
    {
        if (File.Exists(EnvFilePath()) && FileStartsWithOwnershipMarker(EnvFilePath()))
        {
            return true;
        }

        if (
            LegacyEnvFilePath() is { } legacyEnv
            && File.Exists(legacyEnv)
            && FileStartsWithOwnershipMarker(legacyEnv)
        )
        {
            return true;
        }

        if (
            HasOwnedLauncher(
                GetLauncherNames(BrowserLauncherPatchMode.FirefoxEnvironment),
                PrependEnvWrapperToExecLines
            )
        )
        {
            return true;
        }

        if (
            HasOwnedLauncher(
                GetLauncherNames(BrowserLauncherPatchMode.ChromiumRendererAccessibility),
                AddAccessibilityFlagToExecLines
            )
        )
        {
            return true;
        }

        return EnumerateFirefoxProfileDirs()
            .Any(dir => UserJsHasOwnedAccessibilityEntry(Path.Join(dir, "user.js")));
    }

    /// <summary>
    ///     Itemizes what <see cref="RemoveAsync" /> would actually remove right
    ///     now. The Profiles UI feeds this into a confirmation dialog before
    ///     the revert runs, so the user sees every file path that will be
    ///     touched, including which Firefox profile(s) will lose the
    ///     accessibility override.
    /// </summary>
    public static IReadOnlyList<string> DescribeRevertActions()
    {
        var actions = new List<string>();

        if (File.Exists(EnvFilePath()) && FileStartsWithOwnershipMarker(EnvFilePath()))
        {
            actions.Add($"• Delete {EnvFilePath()}");
        }

        if (
            LegacyEnvFilePath() is { } legacyEnv
            && File.Exists(legacyEnv)
            && FileStartsWithOwnershipMarker(legacyEnv)
        )
        {
            actions.Add($"• Delete {legacyEnv}");
        }

        var ownedLaunchers = EnumerateOwnedLauncherPaths().ToList();
        if (ownedLaunchers.Count > 0)
        {
            var sb = new StringBuilder("• Restore or delete patched .desktop launchers:\n");
            foreach (var path in ownedLaunchers)
            {
                var name = Path.GetFileName(path);
                var backupExists = File.Exists(Path.Join(LauncherBackupDir(), name));
                sb.Append("    ").Append(path);
                sb.Append(backupExists ? "  (restore from backup)" : "  (delete)");
                sb.Append('\n');
            }

            actions.Add(sb.ToString().TrimEnd('\n'));
        }

        var profilesWithOwnership = EnumerateFirefoxProfileDirs()
            .Where(dir => UserJsHasOwnedAccessibilityEntry(Path.Join(dir, "user.js")))
            .Select(dir => Path.Join(dir, "user.js"))
            .ToList();
        if (profilesWithOwnership.Count > 0)
        {
            actions.Add(
                "• Remove the TypeWhisper accessibility override line from user.js in:\n"
                + string.Join("\n", profilesWithOwnership.Select(p => "    " + p))
                + "\n  (restore any preserved original accessibility.force_disabled line, "
                + "and delete the file if it becomes empty)"
            );
        }

        return actions;
    }

    /// <summary>
    ///     Prepends the Firefox-family env wrapper to every <c>Exec=</c>
    ///     line in the .desktop content. Inlining the env vars on the
    ///     launcher means accessibility takes effect on every menu launch
    ///     without depending on systemd-user reading
    ///     <c>~/.config/environment.d/</c> — which can silently fail to
    ///     happen across logouts on some session managers.
    /// </summary>
    internal static string PrependEnvWrapperToExecLines(string content)
    {
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith("Exec=", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains("MOZ_ENABLE_ACCESSIBILITY=", StringComparison.Ordinal))
            {
                continue;
            }

            const int prefixEnd = 5; // "Exec=".Length
            lines[i] = "Exec=" + FirefoxEnvWrapper + " " + line[prefixEnd..];
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    ///     Inserts <paramref name="flag" /> into an <c>Exec=</c> line at the
    ///     position the browser actually receives it.
    ///     Naively inserting after the first token breaks Flatpak launchers
    ///     (<c>Exec=/usr/bin/flatpak run org.chromium.Chromium %U</c>) and
    ///     env-wrappers (<c>Exec=env VAR=x /usr/bin/chrome %U</c>) — in those
    ///     cases the wrapper would consume or reject the flag. Anchoring on the
    ///     XDG field-code (<c>%U</c>, <c>%F</c>, ...) or Flatpak escape marker
    ///     (<c>@@</c>) puts the flag in the browser's argument position for
    ///     both wrapped and unwrapped launchers. Falls back to appending when
    ///     the Exec line has no field codes (rare).
    /// </summary>
    internal static string InsertChromiumFlag(string execLine, string flag)
    {
        const int prefixEnd = 5; // "Exec=".Length
        var tailStart = FindFieldCodeOrFlatpakEscape(execLine, prefixEnd);

        if (tailStart < 0)
        {
            return execLine.TrimEnd() + " " + flag;
        }

        var leftEnd = tailStart;
        while (leftEnd > prefixEnd && execLine[leftEnd - 1] == ' ')
        {
            leftEnd--;
        }

        return execLine[..leftEnd] + " " + flag + " " + execLine[tailStart..];
    }

    private static bool IsForceEnabledInProfile(string profileDir)
    {
        // Either user.js (our preferred override file) or Firefox's own
        // prefs.js — whichever already has the right value satisfies us.
        foreach (var name in new[] { "user.js", "prefs.js" })
        {
            var path = Path.Join(profileDir, name);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var content = File.ReadAllText(path);
                if (ForceDisabledNegOneMultilineRegex().IsMatch(content))
                {
                    return true;
                }
            }
            catch
            {
                /* unreadable, skip */
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateFirefoxProfileDirs()
    {
        // Cover all Firefox-family profile locations: ~/.mozilla/firefox (legacy),
        // ~/.config/mozilla (Fedora XDG), ~/snap/... (Snap), ~/.var/app/... (Flatpak),
        // and native/Flatpak fork roots. Missing any means setup claims
        // success while user.js never reaches the browser's actual profile.
        // ReSharper disable once LoopCanBeConvertedToQuery -- the nested form keeps the
        // root-coverage and profile-marker rationale anchored to the code each one explains.
        foreach (var root in FirefoxProfileRootsOverride ?? GetFirefoxProfileRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                // A real profile directory has either prefs.js (after first
                // run) or times.json (created on profile bootstrap). Filter
                // out the Crash Reports / Pending Pings sibling dirs.
                if (
                    File.Exists(Path.Join(dir, "prefs.js"))
                    || File.Exists(Path.Join(dir, "times.json"))
                )
                {
                    yield return dir;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateOwnedLauncherPaths()
    {
        var dir = UserApplicationsDir();
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        var launcherNames = GetLauncherNames(BrowserLauncherPatchMode.FirefoxEnvironment)
            .Concat(
                GetLauncherNames(
                    BrowserLauncherPatchMode.ChromiumRendererAccessibility
                )
            );
        foreach (var name in launcherNames)
        {
            var path = Path.Join(dir, name);
            if (File.Exists(path) && FileStartsWithOwnershipMarker(path))
            {
                yield return path;
            }
        }
    }

    private static bool UserJsHasOwnedAccessibilityEntry(string userJsPath)
    {
        if (!File.Exists(userJsPath))
        {
            return false;
        }

        try
        {
            var content = File.ReadAllText(userJsPath);
            return OwnedAccessibilityEntryRegex().IsMatch(content)
                || PreservedAccessibilityEntryRegex().IsMatch(content);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Adds TypeWhisper's Firefox accessibility override to one profile's
    ///     <c>user.js</c>. Foreign entries for the same preference are commented
    ///     out in place with owned metadata so removal can restore them exactly.
    /// </summary>
    /// <returns><see langword="true" /> when the file changed.</returns>
    internal static bool PatchFirefoxUserJs(string userJsPath)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var snapshot = AtomicFileWriter
                .CaptureAsync(userJsPath, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            var existing = snapshot.Contents;
            // Already effective (our owned block or a user-authored -1): leave the
            // exact file, link, and metadata untouched.
            if (ForceDisabledNegOneMultilineRegex().IsMatch(existing))
            {
                return false;
            }

            var preserved = ForceDisabledAnyValueLineRegex().Replace(
                existing,
                match => UserJsPreservedPrefix + match.Value
            );
            var ownsSeparatorNewline = preserved.Length > 0 && !preserved.EndsWith('\n');
            var prefixNewline = ownsSeparatorNewline ? "\n" : "";
            var ownershipMarker =
                UserJsOwnershipMarker
                + (ownsSeparatorNewline ? UserJsOwnedSeparatorSuffix : "");
            var addition =
                prefixNewline
                + ownershipMarker
                + " — required for AT-SPI URL detection on Wayland.\n"
                + "user_pref(\"accessibility.force_disabled\", -1);\n";

            if (
                AtomicFileWriter
                    .WriteIfUnchangedAsync(
                        snapshot,
                        preserved + addition,
                        CancellationToken.None
                    )
                    .GetAwaiter()
                    .GetResult()
            )
            {
                return true;
            }
        }

        throw new IOException(
            $"'{userJsPath}' kept changing while TypeWhisper tried to update its managed block."
        );
    }

    /// <summary>
    ///     Removes TypeWhisper's Firefox accessibility override from one
    ///     <c>user.js</c> and restores every foreign preference line preserved
    ///     by <see cref="PatchFirefoxUserJs" />.
    /// </summary>
    /// <returns><see langword="true" /> when the file changed or was deleted.</returns>
    internal static bool RevertFirefoxUserJs(string userJsPath)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var snapshot = AtomicFileWriter
                .CaptureAsync(userJsPath, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (!snapshot.Existed)
            {
                return false;
            }

            var content = snapshot.Contents;
            var restored = PreservedAccessibilityEntryRegex().Replace(
                content,
                match => match.Groups["original"].Value
            );
            var stripped = OwnedAccessibilityEntryRegex().Replace(
                restored,
                match =>
                {
                    if (!match.Groups["ownsSeparator"].Success)
                    {
                        return match.Groups["separator"].Value;
                    }

                    // Drop the separator newline we added. The trailing newline is ours
                    // too, but if the user appended content after our block it is the only
                    // thing keeping that content off the preceding line — keep it then.
                    var followedByContent = match.Index + match.Length < restored.Length;
                    return followedByContent ? match.Groups["trailing"].Value : "";
                }
            );

            if (stripped == content)
            {
                return false;
            }

            var requestedIsResolved = string.Equals(
                Path.GetFullPath(snapshot.RequestedTarget),
                snapshot.ResolvedTarget,
                StringComparison.Ordinal
            );
            var committed = string.IsNullOrWhiteSpace(stripped) && requestedIsResolved
                ? AtomicFileWriter
                    .DeleteIfUnchangedAsync(snapshot, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult()
                : AtomicFileWriter
                    .WriteIfUnchangedAsync(snapshot, stripped, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            if (committed)
            {
                return true;
            }
        }

        throw new IOException(
            $"'{userJsPath}' kept changing while TypeWhisper tried to remove its managed block."
        );
    }

    private static List<string> RemoveOwnedFirefoxAccessibilityEntries()
    {
        var cleaned = new List<string>();
        foreach (var profileDir in EnumerateFirefoxProfileDirs())
        {
            var userJsPath = Path.Join(profileDir, "user.js");
            try
            {
                if (RevertFirefoxUserJs(userJsPath))
                {
                    cleaned.Add(Path.GetFileName(profileDir));
                }
            }
            catch
            {
                /* best effort — summary reports what we managed */
            }
        }

        return cleaned;
    }

    private static ManagedFileOperationResult WriteEnvFile()
    {
        var result = CreateManagedTransaction()
            .InstallAsync(BuildEnvFileSpec())
            .GetAwaiter()
            .GetResult();
        // Sweep only once we own the canonical file: if publication was refused, the
        // legacy file may be the user's only working config, and clearing it would
        // leave nothing exporting the variable.
        if (result.OwnsDestination)
        {
            SweepLegacyEnvFile(CancellationToken.None);
        }

        return result;
    }

    /// <summary>
    ///     The pre-XDG release always wrote <c>~/.config/environment.d</c>. When
    ///     XDG_CONFIG_HOME points elsewhere the canonical path no longer covers that
    ///     file, so it is swept separately. Returns null when both paths agree.
    /// </summary>
    private static string? LegacyEnvFilePath()
    {
        var legacy = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config",
            "environment.d",
            EnvFileName
        );
        return string.Equals(EnvFilePath(), legacy, StringComparison.Ordinal) ? null : legacy;
    }

    private static ManagedFileSpec? BuildLegacyEnvFileSpec()
    {
        return LegacyEnvFilePath() is not { } legacyPath
            ? null
            : BuildEnvFileSpec() with
            {
                ArtifactId = "browser-accessibility-environment-legacy",
                DestinationPath = legacyPath,
            };
    }

    /// <summary>
    ///     Deletes the pre-XDG environment file only when it still carries our marker.
    ///     Failures are swallowed: a legacy file we cannot read must not fail the whole
    ///     operation, matching the skip-and-continue rule for removals.
    /// </summary>
    private static void SweepLegacyEnvFile(CancellationToken ct)
    {
        if (BuildLegacyEnvFileSpec() is not { } spec)
        {
            return;
        }

        try
        {
            CreateManagedTransaction().RemoveAsync(spec, ct).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidDataException)
        {
            Trace.WriteLine(
                $"[BrowserAccessibilitySetupHelper] could not sweep '{spec.DestinationPath}': {ex.Message}"
            );
        }
    }

    private static ManagedFileTransaction CreateManagedTransaction()
    {
        return ManagedArtifactStateRootOverride is { } stateRoot
            ? new ManagedFileTransaction(stateRoot)
            : new ManagedFileTransaction();
    }

    private static ManagedFileSpec BuildEnvFileSpec()
    {
        return new ManagedFileSpec
        {
            ArtifactId = "browser-accessibility-environment",
            DestinationPath = EnvFilePath(),
            DesiredBytes = ManagedFileSpec.Utf8(EnvFileContent),
            CreateMode = PrivateConfigMode,
            OwnershipProbe = bytes =>
            {
                using var reader = new StringReader(Encoding.UTF8.GetString(bytes.Span));
                var firstLine = reader.ReadLine();
                return firstLine is not null
                    && firstLine.StartsWith(
                        $"# {OwnershipMarker}",
                        StringComparison.Ordinal
                    );
            },
        };
    }

    private static ManagedFileSpec BuildLauncherSpec(
        string name,
        string destination,
        byte[] desiredBytes,
        Func<string, string> transform,
        string? legacyPreimagePath
    )
    {
        return new ManagedFileSpec
        {
            ArtifactId = $"browser-launcher-{name}",
            DestinationPath = destination,
            DesiredBytes = desiredBytes,
            CreateMode = DesktopFileMode,
            OwnershipProbe = bytes =>
            {
                using var reader = new StringReader(Encoding.UTF8.GetString(bytes.Span));
                return string.Equals(
                    reader.ReadLine(),
                    DesktopOwnershipComment,
                    StringComparison.Ordinal
                );
            },
            ExistingPolicy = ManagedFileExistingPolicy.BackupTransformAndRestore,
            RemovalPolicy = ManagedFileRemovalPolicy.RestorePreimageIfUnchanged,
            BackupTransform = bytes => BuildPatchedLauncherBytes(bytes.ToArray(), transform),
            LegacyPreimagePath = legacyPreimagePath,
        };
    }

    private static byte[] BuildPatchedLauncherBytes(
        byte[] sourceBytes,
        Func<string, string> transform
    )
    {
        var patched = transform(Encoding.UTF8.GetString(sourceBytes));
        return Encoding.UTF8.GetBytes(DesktopOwnershipComment + "\n" + patched);
    }

    private static bool EntryExistsIncludingSymlink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            info.Refresh();
            return info.Exists || info.LinkTarget is not null || Directory.Exists(path);
        }
        catch
        {
            // Treat an uninspectable entry as present so the transaction refuses it.
            return true;
        }
    }

    private static List<string> PatchLaunchers(
        IReadOnlyList<string> names,
        Func<string, string> transformContent
    )
    {
        var userAppsDir = UserApplicationsDir();
        Directory.CreateDirectory(userAppsDir);

        var patched = new List<string>();

        foreach (var name in names)
        {
            var userCopy = Path.Join(userAppsDir, name);
            var userCopyExists = EntryExistsIncludingSymlink(userCopy);
            var backupPath = Path.Join(LauncherBackupDir(), name);
            var legacyBackupExists = EntryExistsIncludingSymlink(backupPath);
            byte[] desiredBytes = [];
            if (!userCopyExists)
            {
                var systemSource = FindSystemLauncher(name);
                if (systemSource is null)
                {
                    continue;
                }

                try
                {
                    desiredBytes = BuildPatchedLauncherBytes(
                        File.ReadAllBytes(systemSource),
                        transformContent
                    );
                }
                catch
                {
                    continue;
                }
            }
            else if (!legacyBackupExists)
            {
                // A pre-transaction shadow without a sidecar can be adopted only when
                // its exact bytes are reproducible from the current system launcher.
                var systemSource = FindSystemLauncher(name);
                if (systemSource is not null)
                {
                    try
                    {
                        desiredBytes = BuildPatchedLauncherBytes(
                            File.ReadAllBytes(systemSource),
                            transformContent
                        );
                    }
                    catch
                    {
                        // Foreign user launchers are transformed from their captured bytes
                        // inside the transaction, so a missing system source is not fatal.
                    }
                }
            }

            var spec = BuildLauncherSpec(
                name,
                userCopy,
                desiredBytes,
                transformContent,
                legacyBackupExists ? backupPath : null
            );
            try
            {
                var result = CreateManagedTransaction()
                    .InstallAsync(spec)
                    .GetAwaiter()
                    .GetResult();
                if (result.OwnsDestination)
                {
                    patched.Add(name);
                }
            }
            catch
            {
                // Per-launcher failures remain isolated, matching the existing behavior.
            }
        }

        return patched;
    }

    private static string AddAccessibilityFlagToExecLines(string content)
    {
        const string flag = "--force-renderer-accessibility";
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith("Exec=", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains(flag, StringComparison.Ordinal))
            {
                continue;
            }

            lines[i] = InsertChromiumFlag(line, flag);
        }

        return string.Join('\n', lines);
    }

    private static int FindFieldCodeOrFlatpakEscape(string line, int searchStart)
    {
        for (var i = searchStart; i < line.Length; i++)
        {
            var c = line[i];
            switch (c)
            {
                case '%' when i + 1 < line.Length:
                    var next = line[i + 1];
                    // %% is an XDG-escaped literal percent — skip both chars so
                    // it doesn't masquerade as a real field code like %f.
                    if (next == '%')
                    {
                        i++;
                        continue;
                    }

                    if (char.IsLetterOrDigit(next))
                    {
                        return i;
                    }

                    break;
                case '@' when i + 1 < line.Length && line[i + 1] == '@':
                    return i;
            }
        }

        return -1;
    }

    /// <summary>
    ///     Launcher source directories in XDG_DATA_DIRS precedence order; the spec
    ///     defaults apply only when that variable is unset. The per-user Flatpak export
    ///     dir leads because <c>flatpak install --user</c> writes there and that copy is
    ///     the one the application menu launches — sourcing a lower-precedence duplicate
    ///     would shadow the launcher with a different browser's Exec line.
    /// </summary>
    internal static IEnumerable<string> LauncherSourceDirectories()
    {
        // Tests pin the search path to a temp dir; production enumerates the real XDG roots.
        return SystemLauncherDirectoriesOverride ?? EnumerateLauncherSourceDirectories();
    }

    private static IEnumerable<string> EnumerateLauncherSourceDirectories()
    {
        var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        var roots = new List<string>
        {
            Path.Join(XdgPaths.ResolveDataHome(), "flatpak", "exports", "share"),
        };
        roots.AddRange(
            string.IsNullOrEmpty(dataDirs)
                ? ["/usr/local/share", "/usr/share"]
                : dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries)
        );
        roots.AddRange(s_flatpakExportRoots);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator -- only the guard is convertible; the body still mutates `seen` and yields.
        foreach (var root in roots)
        {
            // The XDG spec says relative entries are invalid and must be ignored.
            if (!Path.IsPathRooted(root))
            {
                continue;
            }

            var dir = Path.Join(root, "applications");
            if (seen.Add(dir.TrimEnd('/')))
            {
                yield return dir;
            }
        }
    }

    internal static string? FindSystemLauncher(string name)
    {
        return LauncherSourceDirectories()
            .Select(dir => Path.Join(dir, name))
            .FirstOrDefault(File.Exists);
    }

    private static bool HasOwnedLauncher(
        IReadOnlyList<string> launcherNames,
        Func<string, string> transform
    )
    {
        var dir = UserApplicationsDir();
        return Directory.Exists(dir)
            && launcherNames.Any(name => LauncherIsManaged(name, transform));
    }

    /// <summary>
    ///     True when every installed launcher in <paramref name="launcherNames" /> has a
    ///     patched shadow in the user applications directory (vacuously true if none are
    ///     installed). Distinct from <see cref="HasOwnedLauncher" />, which only tests
    ///     whether *any* launcher was patched (used by <see cref="HasInstalledChanges" />).
    /// </summary>
    private static bool AllInstalledLaunchersOwned(
        IReadOnlyList<string> launcherNames,
        Func<string, string> transform
    )
    {
        var userDir = UserApplicationsDir();
        // ReSharper disable once LoopCanBeConvertedToQuery -- the continue-skip over not-installed launchers mirrors the documented "every installed launcher is owned" semantics more clearly than a chained Where/All.
        foreach (var name in launcherNames)
        {
            var isInstalled =
                FindSystemLauncher(name) is not null
                || HasUserOwnedOrNonOwnedLauncher(userDir, name);
            if (!isInstalled)
            {
                continue;
            }

            if (!LauncherIsManaged(name, transform))
            {
                return false;
            }
        }

        return true;
    }

    private static bool LauncherIsManaged(
        string name,
        Func<string, string> transform
    )
    {
        var destination = Path.Join(UserApplicationsDir(), name);
        var backupPath = Path.Join(LauncherBackupDir(), name);
        var legacyBackupExists = EntryExistsIncludingSymlink(backupPath);
        byte[] desired = [];
        var systemSource = FindSystemLauncher(name);
        if (systemSource is not null)
        {
            try
            {
                desired = BuildPatchedLauncherBytes(File.ReadAllBytes(systemSource), transform);
            }
            catch
            {
                return false;
            }
        }

        try
        {
            var classification = CreateManagedTransaction().Probe(
                BuildLauncherSpec(
                    name,
                    destination,
                    desired,
                    transform,
                    legacyBackupExists ? backupPath : null
                )
            );
            return classification is ManagedFileClassification.CurrentOwned
                or ManagedFileClassification.StaleOwned;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasUserOwnedOrNonOwnedLauncher(string userDir, string name)
    {
        var path = Path.Join(userDir, name);
        return File.Exists(path);
    }

    private static bool HasInstalledLauncher(IReadOnlyList<string> launcherNames)
    {
        var userDir = UserApplicationsDir();
        foreach (var name in launcherNames)
        {
            // A patched shadow doesn't count as "installed" — only non-owned user launchers
            // and system launchers do, so an env-file-only run isn't treated as installed.
            var userPath = Path.Join(userDir, name);
            if (File.Exists(userPath) && !FileStartsWithOwnershipMarker(userPath))
            {
                return true;
            }

            if (FindSystemLauncher(name) is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<List<string>> RemoveOwnedLaunchers(CancellationToken ct)
    {
        var dir = UserApplicationsDir();
        var removed = new List<string>();
        var launchers = GetLauncherNames(BrowserLauncherPatchMode.FirefoxEnvironment)
            .Select(name => (name, transform: (Func<string, string>)PrependEnvWrapperToExecLines))
            .Concat(
                GetLauncherNames(BrowserLauncherPatchMode.ChromiumRendererAccessibility)
                    .Select(name => (name, transform: (Func<string, string>)AddAccessibilityFlagToExecLines))
            );
        foreach (var (name, transform) in launchers)
        {
            var file = Path.Join(dir, name);
            var backupPath = Path.Join(LauncherBackupDir(), name);
            var legacyBackupExists = EntryExistsIncludingSymlink(backupPath);
            byte[] desired = [];
            var systemSource = FindSystemLauncher(name);
            try
            {
                if (systemSource is not null)
                {
                    desired = BuildPatchedLauncherBytes(
                        await File.ReadAllBytesAsync(systemSource, ct).ConfigureAwait(false),
                        transform
                    );
                }

                var result = await CreateManagedTransaction()
                    .RemoveAsync(
                        BuildLauncherSpec(
                            name,
                            file,
                            desired,
                            transform,
                            legacyBackupExists ? backupPath : null
                        ),
                        ct
                    )
                    .ConfigureAwait(false);
                if (result.Changed)
                {
                    removed.Add(name + (legacyBackupExists ? " (restored)" : string.Empty));
                    if (legacyBackupExists)
                    {
                        // Spent now that the preimage restored the launcher — delete before it
                        // accumulates as stale legacy-ownership evidence. No marker check
                        // needed: we created this backup ourselves.
                        try
                        {
                            File.Delete(backupPath);
                        }
                        catch (Exception ex) when (ex is IOException
                                                       or UnauthorizedAccessException)
                        {
                            Trace.WriteLine(
                                $"[BrowserAccessibilitySetupHelper] could not delete spent backup '{backupPath}': {ex.Message}"
                            );
                        }
                    }
                }
            }
            catch
            {
                /* best effort */
            }
        }

        return removed;
    }

    private static bool FileStartsWithOwnershipMarker(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            var firstLine = reader.ReadLine();
            return firstLine is not null
                   && firstLine.StartsWith("# " + OwnershipMarker, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string EnvFilePath()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            configHome = Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config"
            );
        }

        return Path.Join(configHome, "environment.d", EnvFileName);
    }

    private static string UserApplicationsDir()
    {
        return Path.Join(XdgPaths.ResolveDataHome(), "applications");
    }

    private static string LauncherBackupDir()
    {
        return Path.Join(XdgPaths.ResolveDataHome(), "typewhisper", "launcher-backups");
    }

    public sealed record Status(
        bool FirefoxEnvFilePresent,
        bool FirefoxLauncherPresent,
        bool ChromiumLauncherPresent,
        bool FirefoxInstalled,
        bool ChromiumInstalled,
        bool FirefoxAccessibilityForceEnabled,
        bool FirefoxProfileFound
    )
    {
        /// <summary>
        ///     True when every installed browser family is patched and Firefox's accessibility
        ///     lazy-init gate is force-enabled (<c>accessibility.force_disabled = -1</c> —
        ///     modern Firefox ignores env vars without it). The Firefox pref check is skipped
        ///     when no profile has been created yet.
        /// </summary>
        public bool IsFullyConfigured =>
            FirefoxEnvFilePresent
            && (!FirefoxInstalled || FirefoxLauncherPresent)
            && (!ChromiumInstalled || ChromiumLauncherPresent)
            && (!FirefoxInstalled || !FirefoxProfileFound || FirefoxAccessibilityForceEnabled);
    }

    // accessibility.force_disabled = -1 pref line, matched per-line across full user.js content
    // (Multiline so the line is recognized even when our attribution comment precedes it). Accepts
    // either quote style, like ForceDisabledAnyValueLineRegex: Firefox's pref parser takes both, so
    // a single-quoted user-authored -1 is already effective and must not be rewritten/preserved.
    [GeneratedRegex("""^\s*user_pref\(\s*(?<quote>["'])accessibility\.force_disabled\k<quote>\s*,\s*-1\s*\)\s*;""", RegexOptions.Multiline)]
    private static partial Regex ForceDisabledNegOneMultilineRegex();

    // Any live accessibility.force_disabled line, captured verbatim (minus its
    // newline) so setup can prefix it with the preservation comment. Lines where a
    // second statement follows the terminator are skipped: commenting the whole
    // line would disable the neighbor too (our appended -1 wins regardless).
    [GeneratedRegex("""^[\t ]*user_pref\s*\(\s*(?<quote>["'])accessibility\.force_disabled\k<quote>\s*,[^\r\n;)]*\)\s*;[\t ]*(?://[^\r\n]*)?(?=\r?\n|\z)""", RegexOptions.Multiline)]
    private static partial Regex ForceDisabledAnyValueLineRegex();

    // A foreign pref line disabled by setup. Removing the prefix restores every byte
    // of the original line while leaving its original line ending untouched.
    [GeneratedRegex("""^// TypeWhisper preserved: (?<original>[^\r\n]*)(?=\r?\n|\z)""", RegexOptions.Multiline)]
    private static partial Regex PreservedAccessibilityEntryRegex();

    // Our attribution comment plus the following force_disabled=-1 pref line. The
    // optional preceding newline is retained unless its marker says setup added it.
    [GeneratedRegex("""(?<separator>\r?\n)?^//\s*Set by TypeWhisper(?<ownsSeparator>; separator newline owned)?[^\r\n]*\r?\nuser_pref\(\s*"accessibility\.force_disabled"\s*,\s*-1\s*\)[\t ]*;[\t ]*(?<trailing>\r?\n|\z)""", RegexOptions.Multiline)]
    private static partial Regex OwnedAccessibilityEntryRegex();

    public sealed record SetupResult(bool Success, string Message, string? Detail = null);
}
