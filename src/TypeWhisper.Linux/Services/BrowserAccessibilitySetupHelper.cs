using System.Text;
using System.Text.RegularExpressions;

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

    private static readonly string[] s_chromiumLauncherNames =
    [
        "google-chrome.desktop",
        "chromium.desktop",
        "chromium-browser.desktop",
        "microsoft-edge.desktop",
        "brave-browser.desktop",
        "vivaldi-stable.desktop",
        "opera.desktop"
    ];

    private static readonly string[] s_firefoxLauncherNames =
    [
        "firefox.desktop",
        "org.mozilla.firefox.desktop",
        "firefox-esr.desktop",
        "librewolf.desktop",
        "io.gitlab.librewolf-community.desktop",
        "zen.desktop",
        "app.zen_browser.zen.desktop",
        "io.github.zen_browser.zen.desktop"
    ];

    private static readonly string[] s_systemLauncherDirectories =
    [
        "/usr/share/applications",
        "/var/lib/flatpak/exports/share/applications"
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

    public static Status IsCurrentlyConfigured()
    {
        var envFilePresent = File.Exists(EnvFilePath());
        // "LauncherPresent" means every installed launcher in this family is patched, not just
        // one — a newly-installed browser (e.g. Brave after Chrome was already patched) must not
        // silently count as configured while its own launcher still lacks the flag.
        var firefoxLauncherPresent = AllInstalledLaunchersOwned(s_firefoxLauncherNames);
        var chromiumLauncherPresent = AllInstalledLaunchersOwned(s_chromiumLauncherNames);
        var firefoxInstalled = HasInstalledLauncher(s_firefoxLauncherNames);
        var chromiumInstalled = HasInstalledLauncher(s_chromiumLauncherNames);
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
                    var existing = File.Exists(userJsPath) ? File.ReadAllText(userJsPath) : "";

                    if (ForceDisabledNegOneMultilineRegex().IsMatch(existing))
                    {
                        patched.Add(Path.GetFileName(profileDir));
                        continue;
                    }

                    // Replace any other accessibility.force_disabled line so
                    // we don't leave two contradictory pref entries.
                    var cleaned = ForceDisabledAnyValueLineRegex().Replace(existing, "");

                    var prefixNewline = cleaned.Length > 0 && !cleaned.EndsWith('\n') ? "\n" : "";
                    var addition =
                        prefixNewline
                        + "// Set by TypeWhisper — required for AT-SPI URL detection on Wayland.\n"
                        + "user_pref(\"accessibility.force_disabled\", -1);\n";

                    var tmp = userJsPath + ".tmp";
                    File.WriteAllText(tmp, cleaned + addition);
                    File.Move(tmp, userJsPath, true);
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
    public Task<SetupResult> SetUpAsync(CancellationToken ct)
    {
        try
        {
            WriteEnvFile();
            var chromiumPatched = PatchLaunchers(
                s_chromiumLauncherNames,
                AddAccessibilityFlagToExecLines
            );
            var firefoxPatched = PatchLaunchers(s_firefoxLauncherNames, PrependEnvWrapperToExecLines);
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
            + "  Appends user_pref(\"accessibility.force_disabled\", -1); — Firefox reads user.js\n"
            + "  at every startup as the override file and never writes back to it."
        );

        return actions;
    }

    public static Task<SetupResult> RemoveAsync(CancellationToken ct)
    {
        try
        {
            var removedEnv = TryRemoveOwnedFile(EnvFilePath());
            var removedLaunchers = RemoveOwnedLaunchers();
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
                return Task.FromResult(new SetupResult(true, summary.ToString()));
            }

            summary.Append(' ');
            summary.Append("Cleaned Firefox profile(s): ");
            summary.Append(string.Join(", ", cleanedProfiles));
            summary.Append('.');

            return Task.FromResult(new SetupResult(true, summary.ToString()));
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                new SetupResult(
                    false,
                    "Could not remove browser accessibility integration.",
                    ex.Message
                )
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

        if (HasOwnedLauncher(s_firefoxLauncherNames))
        {
            return true;
        }

        if (HasOwnedLauncher(s_chromiumLauncherNames))
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
                + "\n  (delete the file if it becomes empty)"
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
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Cover all Firefox-family profile locations: ~/.mozilla/firefox (legacy),
        // ~/.config/mozilla (Fedora XDG), ~/snap/... (Snap), ~/.var/app/... (Flatpak),
        // and ~/.zen / ~/.librewolf (native forks). Missing any means setup claims
        // success while user.js never reaches the browser's actual profile.
        var roots = new[]
        {
            Path.Join(home, ".mozilla", "firefox"), Path.Join(home, ".config", "mozilla", "firefox"),
            Path.Join(home, "snap", "firefox", "common", ".mozilla", "firefox"),
            Path.Join(home, ".var", "app", "org.mozilla.firefox", ".mozilla", "firefox"),
            Path.Join(home, ".var", "app", "app.zen_browser.zen", ".zen"),
            Path.Join(home, ".var", "app", "io.github.zen_browser.zen", ".zen"), Path.Join(home, ".zen"),
            Path.Join(home, ".var", "app", "io.gitlab.librewolf-community", ".librewolf"),
            Path.Join(home, ".librewolf")
        };
        foreach (var root in roots)
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

        foreach (var name in s_firefoxLauncherNames.Concat(s_chromiumLauncherNames))
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
            // Owned entries are flagged by our attribution comment that
            // immediately precedes the pref line we wrote. We never claim
            // ownership of a bare user_pref that was hand-added.
            return content.Contains(UserJsOwnershipMarker, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static List<string> RemoveOwnedFirefoxAccessibilityEntries()
    {
        var cleaned = new List<string>();
        foreach (var profileDir in EnumerateFirefoxProfileDirs())
        {
            var userJsPath = Path.Join(profileDir, "user.js");
            if (!UserJsHasOwnedAccessibilityEntry(userJsPath))
            {
                continue;
            }

            try
            {
                var content = File.ReadAllText(userJsPath);
                // Strip the attribution comment plus the following pref line
                // in a single match. The user's other prefs (if any) stay
                // untouched, including a manually-added force_disabled that
                // happens to share the value — we only remove the pair we
                // wrote ourselves, identified by the comment marker.
                const string pattern =
                    @"^//\s*Set by TypeWhisper[^\r\n]*\r?\n"
                    + @"user_pref\(\s*""accessibility\.force_disabled""\s*,\s*-1\s*\)\s*;\s*\r?\n?";
                var stripped = Regex.Replace(content, pattern, "", RegexOptions.Multiline);

                if (stripped == content)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(stripped))
                {
                    File.Delete(userJsPath);
                }
                else
                {
                    var tmp = userJsPath + ".tmp";
                    File.WriteAllText(tmp, stripped);
                    File.Move(tmp, userJsPath, true);
                }

                cleaned.Add(Path.GetFileName(profileDir));
            }
            catch
            {
                /* best effort — summary reports what we managed */
            }
        }

        return cleaned;
    }

    private static void WriteEnvFile()
    {
        var path = EnvFilePath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, EnvFileContent);
        File.Move(tempPath, path, true);
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
            var userCopyExists = File.Exists(userCopy);

            if (userCopyExists && FileStartsWithOwnershipMarker(userCopy))
            {
                patched.Add(name);
                continue;
            }

            string sourceContent;
            if (userCopyExists)
            {
                // Non-owned user launcher: preserve it via sidecar backup so RemoveAsync
                // can restore the user's customizations (Exec wrappers, env, icons, etc.).
                // Patch from the user's own content rather than the system copy so we
                // don't clobber their changes.
                try
                {
                    sourceContent = File.ReadAllText(userCopy);
                }
                catch
                {
                    continue;
                }

                if (!TryBackupUserLauncher(userCopy, name))
                {
                    continue;
                }
            }
            else
            {
                var systemSource = FindSystemLauncher(name);
                if (systemSource is null)
                {
                    continue;
                }

                try
                {
                    sourceContent = File.ReadAllText(systemSource);
                }
                catch
                {
                    continue;
                }
            }

            var patchedContent = transformContent(sourceContent);
            var finalContent = DesktopOwnershipComment + "\n" + patchedContent;

            var tempPath = userCopy + ".tmp";
            File.WriteAllText(tempPath, finalContent);
            File.Move(tempPath, userCopy, true);
            patched.Add(name);
        }

        return patched;
    }

    private static bool TryBackupUserLauncher(string userCopy, string name)
    {
        try
        {
            var backupDir = LauncherBackupDir();
            Directory.CreateDirectory(backupDir);
            var backupPath = Path.Join(backupDir, name);
            // Preserve the oldest backup if we ran setup multiple times.
            if (!File.Exists(backupPath))
            {
                File.Copy(userCopy, backupPath, false);
            }

            return true;
        }
        catch
        {
            return false;
        }
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

    private static string? FindSystemLauncher(string name)
    {
        foreach (var dir in s_systemLauncherDirectories)
        {
            var candidate = Path.Join(dir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool HasOwnedLauncher(IReadOnlyList<string> launcherNames)
    {
        var dir = UserApplicationsDir();
        if (!Directory.Exists(dir))
        {
            return false;
        }

        foreach (var name in launcherNames)
        {
            var path = Path.Join(dir, name);
            if (File.Exists(path) && FileStartsWithOwnershipMarker(path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     True when every installed launcher in <paramref name="launcherNames" /> has a
    ///     patched shadow in the user applications directory (vacuously true if none are
    ///     installed). Distinct from <see cref="HasOwnedLauncher" />, which only tests
    ///     whether *any* launcher was patched (used by <see cref="HasInstalledChanges" />).
    /// </summary>
    private static bool AllInstalledLaunchersOwned(IReadOnlyList<string> launcherNames)
    {
        var userDir = UserApplicationsDir();
        foreach (var name in launcherNames)
        {
            var isInstalled =
                FindSystemLauncher(name) is not null
                || HasUserOwnedOrNonOwnedLauncher(userDir, name);
            if (!isInstalled)
            {
                continue;
            }

            var ownedShadow = Path.Join(userDir, name);
            if (!File.Exists(ownedShadow) || !FileStartsWithOwnershipMarker(ownedShadow))
            {
                return false;
            }
        }

        return true;
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

    private static List<string> RemoveOwnedLaunchers()
    {
        var dir = UserApplicationsDir();
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var backupDir = LauncherBackupDir();
        var removed = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.desktop"))
        {
            if (!FileStartsWithOwnershipMarker(file))
            {
                continue;
            }

            var name = Path.GetFileName(file);
            var backupPath = Path.Join(backupDir, name);

            try
            {
                if (File.Exists(backupPath))
                {
                    // Atomic restore: write to .tmp then move.
                    var tempPath = file + ".restore.tmp";
                    File.Copy(backupPath, tempPath, true);
                    File.Move(tempPath, file, true);
                    try
                    {
                        File.Delete(backupPath);
                    }
                    catch
                    {
                        /* best effort */
                    }

                    removed.Add(name + " (restored)");
                }
                else
                {
                    File.Delete(file);
                    removed.Add(name);
                }
            }
            catch
            {
                /* best effort */
            }
        }

        try
        {
            if (
                Directory.Exists(backupDir)
                && !Directory.EnumerateFileSystemEntries(backupDir).Any()
            )
            {
                Directory.Delete(backupDir);
            }
        }
        catch
        {
            /* best effort */
        }

        return removed;
    }

    private static bool TryRemoveOwnedFile(string path)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        if (!FileStartsWithOwnershipMarker(path))
        {
            return false;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
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
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Join(home, ".config", "environment.d", EnvFileName);
    }

    private static string UserApplicationsDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Join(home, ".local", "share", "applications");
    }

    private static string LauncherBackupDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Join(home, ".local", "share", "typewhisper", "launcher-backups");
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
    // (Multiline so the line is recognized even when our attribution comment precedes it).
    [GeneratedRegex(@"^\s*user_pref\(\s*""accessibility\.force_disabled""\s*,\s*-1\s*\)\s*;", RegexOptions.Multiline)]
    private static partial Regex ForceDisabledNegOneMultilineRegex();

    // Any accessibility.force_disabled line (any value) so we don't leave contradictory prefs.
    [GeneratedRegex(@"^\s*user_pref\(\s*""accessibility\.force_disabled""\s*,\s*-?\d+\s*\)\s*;\s*\r?\n?", RegexOptions.Multiline)]
    private static partial Regex ForceDisabledAnyValueLineRegex();

    public sealed record SetupResult(bool Success, string Message, string? Detail = null);
}