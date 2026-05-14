using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// One-click installer for the browser accessibility surface that powers
/// AT-SPI URL detection. Mirrors the shape of
/// <see cref="Insertion.YdotoolSetupHelper"/>: an <see cref="IsCurrentlyConfigured"/>
/// snapshot, a <see cref="SetUpAsync"/> action, and a matching
/// <see cref="RemoveAsync"/> rollback.
///
/// Two artifacts get installed:
///   1. <c>~/.config/environment.d/typewhisper-accessibility.conf</c> —
///      exports <c>MOZ_ENABLE_ACCESSIBILITY=1</c> so Firefox/Zen expose
///      their address bar over AT-SPI, plus <c>GTK_MODULES=gail:atk-bridge</c>
///      for GTK apps that rely on the legacy module path.
///   2. User-local <c>.desktop</c> overrides in
///      <c>~/.local/share/applications/</c> for each Chromium-family
///      browser, adding <c>--force-renderer-accessibility</c> to the
///      <c>Exec=</c> line. The user-local copy shadows the
///      system-installed launcher without modifying it.
///
/// Both artifacts carry the <see cref="OwnershipMarker"/> in their first
/// line so <see cref="RemoveAsync"/> can confirm we own them before
/// deletion — avoids nuking a config the user wrote themselves.
/// </summary>
public sealed class BrowserAccessibilitySetupHelper
{
    internal const string OwnershipMarker = "Installed by TypeWhisper";

    private const string EnvFileName = "typewhisper-accessibility.conf";
    private const string EnvFileContent =
        "# " + OwnershipMarker + " — enables Firefox / GTK accessibility so\n" +
        "# the dictation overlay can read your browser URL for profile\n" +
        "# matching. Remove this file (and rerun) to roll back.\n" +
        "MOZ_ENABLE_ACCESSIBILITY=1\n" +
        "GTK_MODULES=gail:atk-bridge\n";

    private const string DesktopOwnershipComment =
        "# " + OwnershipMarker + " - patches Exec= for URL detection";

    private const string FirefoxEnvWrapper = "env MOZ_ENABLE_ACCESSIBILITY=1 GTK_MODULES=gail:atk-bridge";

    private static readonly string[] ChromiumLauncherNames =
    [
        "google-chrome.desktop",
        "chromium.desktop",
        "chromium-browser.desktop",
        "microsoft-edge.desktop",
        "brave-browser.desktop",
        "vivaldi-stable.desktop",
        "opera.desktop",
    ];

    private static readonly string[] FirefoxLauncherNames =
    [
        "firefox.desktop",
        "org.mozilla.firefox.desktop",
        "firefox-esr.desktop",
        "librewolf.desktop",
        "io.gitlab.librewolf-community.desktop",
        "zen.desktop",
        "app.zen_browser.zen.desktop",
        "io.github.zen_browser.zen.desktop",
    ];

    private static readonly string[] SystemLauncherDirectories =
    [
        "/usr/share/applications",
        "/var/lib/flatpak/exports/share/applications",
    ];

    public sealed record Status(
        bool FirefoxEnvFilePresent,
        bool FirefoxLauncherPresent,
        bool ChromiumLauncherPresent,
        bool FirefoxInstalled,
        bool ChromiumInstalled,
        bool FirefoxAccessibilityForceEnabled,
        bool FirefoxProfileFound)
    {
        /// <summary>
        /// True when every installed browser family has been patched
        /// AND Firefox's accessibility lazy-init gate is force-enabled
        /// (modern Firefox refuses to register on AT-SPI without
        /// <c>accessibility.force_disabled = -1</c>, regardless of env
        /// vars). We only require the Firefox pref check when we've
        /// actually found a Firefox profile to read — fresh Firefox
        /// installs that haven't created a profile yet shouldn't be
        /// flagged as misconfigured.
        /// </summary>
        public bool IsFullyConfigured =>
            FirefoxEnvFilePresent
            && (!FirefoxInstalled || FirefoxLauncherPresent)
            && (!ChromiumInstalled || ChromiumLauncherPresent)
            && (!FirefoxInstalled || !FirefoxProfileFound || FirefoxAccessibilityForceEnabled);
    }

    /// <summary>
    /// True only on Wayland sessions, where the AT-SPI walker is the
    /// only way to capture a browser's current URL. On X11 the existing
    /// xdotool + xclip Ctrl+L/Ctrl+C path already covers URL capture,
    /// so prompting the user to enable browser accessibility there
    /// would just be noise.
    /// </summary>
    public bool IsApplicable()
    {
        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        return string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase);
    }

    public sealed record SetupResult(bool Success, string Message, string? Detail = null);

    public Status IsCurrentlyConfigured()
    {
        var envFilePresent = File.Exists(EnvFilePath());
        // FirefoxLauncherPresent / ChromiumLauncherPresent here means
        // "every installed launcher in this family is patched", not
        // "at least one is" — otherwise installing a new browser
        // (e.g. Brave after Chrome was already patched) would silently
        // count as "configured" while Brave's launcher still misses
        // --force-renderer-accessibility. This matches the Status
        // record's contract that IsFullyConfigured implies the
        // integration actually works for every supported browser.
        var firefoxLauncherPresent = AllInstalledLaunchersOwned(FirefoxLauncherNames);
        var chromiumLauncherPresent = AllInstalledLaunchersOwned(ChromiumLauncherNames);
        var firefoxInstalled = HasInstalledLauncher(FirefoxLauncherNames);
        var chromiumInstalled = HasInstalledLauncher(ChromiumLauncherNames);
        var firefoxProfiles = EnumerateFirefoxProfileDirs().ToList();
        var firefoxProfileFound = firefoxProfiles.Count > 0;
        // ALL profiles need the override — the setup path writes to every
        // discovered profile, so detection must require every profile too.
        // Reporting "configured" when only one of N profiles has the pref
        // would hide the Enable button while dictation from any of the
        // other profiles still silently fails URL detection.
        var firefoxForceEnabled = firefoxProfileFound && firefoxProfiles
            .All(IsForceEnabledInProfile);
        return new Status(
            envFilePresent,
            firefoxLauncherPresent,
            chromiumLauncherPresent,
            firefoxInstalled,
            chromiumInstalled,
            firefoxForceEnabled,
            firefoxProfileFound);
    }

    /// <summary>
    /// Writes <c>user_pref("accessibility.force_disabled", -1);</c> to
    /// every discoverable Firefox profile's <c>user.js</c>. Firefox
    /// reads <c>user.js</c> on every startup and uses it as the
    /// authoritative override for <c>prefs.js</c>, so this is the safe
    /// way to script-apply a pref — Firefox itself never writes back to
    /// <c>user.js</c>, and a running Firefox won't clobber our change on
    /// its next save. Takes effect on the next Firefox restart.
    /// </summary>
    public SetupResult ForceEnableFirefoxAccessibility()
    {
        var patched = new List<string>();
        var skipped = new List<string>();

        try
        {
            foreach (var profileDir in EnumerateFirefoxProfileDirs())
            {
                var userJsPath = Path.Combine(profileDir, "user.js");
                try
                {
                    var existing = File.Exists(userJsPath) ? File.ReadAllText(userJsPath) : "";

                    if (Regex.IsMatch(existing, ForceDisabledNegOnePattern))
                    {
                        patched.Add(Path.GetFileName(profileDir));
                        continue;
                    }

                    // Replace any other accessibility.force_disabled line so
                    // we don't leave two contradictory pref entries.
                    var cleaned = Regex.Replace(
                        existing,
                        @"^\s*user_pref\(\s*""accessibility\.force_disabled""\s*,\s*-?\d+\s*\)\s*;\s*\r?\n?",
                        "",
                        RegexOptions.Multiline);

                    var prefixNewline = cleaned.Length > 0 && !cleaned.EndsWith('\n') ? "\n" : "";
                    var addition = prefixNewline +
                        $"// Set by TypeWhisper — required for AT-SPI URL detection on Wayland.\n" +
                        $"user_pref(\"accessibility.force_disabled\", -1);\n";

                    var tmp = userJsPath + ".tmp";
                    File.WriteAllText(tmp, cleaned + addition);
                    File.Move(tmp, userJsPath, overwrite: true);
                    patched.Add(Path.GetFileName(profileDir));
                }
                catch
                {
                    skipped.Add(Path.GetFileName(profileDir));
                }
            }

            if (patched.Count == 0 && skipped.Count == 0)
                return new SetupResult(false,
                    "No Firefox profiles were found to patch.",
                    "Run Firefox once to create a profile, then try again.");

            var detail = new StringBuilder();
            if (patched.Count > 0)
            {
                detail.Append("Patched profile(s): ").Append(string.Join(", ", patched)).Append('.');
            }
            if (skipped.Count > 0)
            {
                if (detail.Length > 0) detail.Append(' ');
                detail.Append("Could not write to: ").Append(string.Join(", ", skipped)).Append('.');
            }
            detail.Append(" Restart Firefox for the change to take effect.");

            return new SetupResult(patched.Count > 0,
                patched.Count > 0
                    ? "Firefox accessibility force-enabled."
                    : "Could not enable Firefox accessibility.",
                detail.ToString());
        }
        catch (Exception ex)
        {
            return new SetupResult(false, "Could not enable Firefox accessibility.", ex.Message);
        }
    }

    private const string ForceDisabledNegOnePattern =
        @"^\s*user_pref\(\s*""accessibility\.force_disabled""\s*,\s*-1\s*\)\s*;";

    private static bool IsForceEnabledInProfile(string profileDir)
    {
        // Either user.js (our preferred override file) or Firefox's own
        // prefs.js — whichever already has the right value satisfies us.
        foreach (var name in new[] { "user.js", "prefs.js" })
        {
            var path = Path.Combine(profileDir, name);
            if (!File.Exists(path)) continue;
            try
            {
                var content = File.ReadAllText(path);
                if (Regex.IsMatch(content, ForceDisabledNegOnePattern, RegexOptions.Multiline))
                    return true;
            }
            catch { /* unreadable, skip */ }
        }
        return false;
    }

    private static IEnumerable<string> EnumerateFirefoxProfileDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Cover every place a Firefox-family browser stores its profile:
        //   - ~/.mozilla/firefox    legacy default
        //   - ~/.config/mozilla     Fedora's XDG-compliant layout
        //   - ~/snap/...            Snap-wrapped Firefox
        //   - ~/.var/app/<id>/...   Flatpak-wrapped Firefox / Zen / LibreWolf
        //   - ~/.zen, ~/.librewolf  native Zen / LibreWolf installs
        // Zen and LibreWolf are Firefox forks that follow the same
        // profile-dir conventions but use their own sandbox IDs and
        // top-level dot-dirs. Missing any of these means our setup
        // claims success while the user.js override never reaches the
        // browser's actual profile.
        var roots = new[]
        {
            Path.Combine(home, ".mozilla", "firefox"),
            Path.Combine(home, ".config", "mozilla", "firefox"),
            Path.Combine(home, "snap", "firefox", "common", ".mozilla", "firefox"),
            Path.Combine(home, ".var", "app", "org.mozilla.firefox", ".mozilla", "firefox"),
            Path.Combine(home, ".var", "app", "app.zen_browser.zen", ".zen"),
            Path.Combine(home, ".var", "app", "io.github.zen_browser.zen", ".zen"),
            Path.Combine(home, ".zen"),
            Path.Combine(home, ".var", "app", "io.gitlab.librewolf-community", ".librewolf"),
            Path.Combine(home, ".librewolf"),
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                // A real profile directory has either prefs.js (after first
                // run) or times.json (created on profile bootstrap). Filter
                // out the Crash Reports / Pending Pings sibling dirs.
                if (File.Exists(Path.Combine(dir, "prefs.js"))
                    || File.Exists(Path.Combine(dir, "times.json")))
                    yield return dir;
            }
        }
    }

    public Task<SetupResult> SetUpAsync(CancellationToken ct)
    {
        try
        {
            WriteEnvFile();
            var chromiumPatched = PatchLaunchers(ChromiumLauncherNames, AddAccessibilityFlagToExecLines);
            var firefoxPatched = PatchLaunchers(FirefoxLauncherNames, PrependEnvWrapperToExecLines);
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
                if (detail.Length > 0) detail.Append('\n');
                detail.Append("Chromium launchers patched: ");
                detail.Append(string.Join(", ", chromiumPatched));
                detail.Append('.');
            }
            if (firefoxPrefResult.Success && !string.IsNullOrWhiteSpace(firefoxPrefResult.Detail))
            {
                if (detail.Length > 0) detail.Append('\n');
                detail.Append("Firefox accessibility: ").Append(firefoxPrefResult.Detail);
            }
            if (firefoxPatched.Count == 0 && chromiumPatched.Count == 0)
            {
                detail.Append("No browser launchers were found on this system; only the user-wide env file was written.");
            }
            else
            {
                detail.Append('\n');
                detail.Append("Fully quit the affected browsers and relaunch from the application menu — running instances are not retroactively patched.");
            }

            var success = firefoxPrefResult.Success
                || firefoxPatched.Count > 0
                || chromiumPatched.Count > 0
                || IsCurrentlyConfigured().IsFullyConfigured;

            return Task.FromResult(new SetupResult(success,
                success ? "Browser accessibility enabled." : "Could not enable browser accessibility.",
                detail.ToString()));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new SetupResult(false,
                "Could not enable browser accessibility.",
                ex.Message));
        }
    }

    /// <summary>
    /// Returns a human-readable list of the changes <see cref="SetUpAsync"/>
    /// would actually make right now. The Profiles UI shows this in the
    /// confirmation dialog so the user can see what's about to be touched
    /// on disk — file paths included — before approving. Items already
    /// done in a prior run are omitted from the list so the dialog never
    /// over-claims what it's doing.
    /// </summary>
    public IReadOnlyList<string> DescribePendingActions()
    {
        var status = IsCurrentlyConfigured();
        var actions = new List<string>();

        if (!status.FirefoxEnvFilePresent)
        {
            actions.Add(
                $"• Write {EnvFilePath()}\n" +
                "  Sets MOZ_ENABLE_ACCESSIBILITY=1 and GTK_MODULES=gail:atk-bridge user-wide.");
        }

        if (status.FirefoxInstalled && !status.FirefoxLauncherPresent)
        {
            actions.Add(
                $"• Shadow Firefox / Zen .desktop launchers in {UserApplicationsDir()}\n" +
                "  Adds env MOZ_ENABLE_ACCESSIBILITY=1 to the Exec= line so the env arrives even\n" +
                "  if systemd-user did not reload environment.d across a logout.");
        }

        if (status.ChromiumInstalled && !status.ChromiumLauncherPresent)
        {
            actions.Add(
                $"• Shadow Chromium-family .desktop launchers in {UserApplicationsDir()}\n" +
                "  Adds the --force-renderer-accessibility flag to Exec=.");
        }

        if (status.FirefoxInstalled && status.FirefoxProfileFound && !status.FirefoxAccessibilityForceEnabled)
        {
            var profiles = EnumerateFirefoxProfileDirs().Select(d => Path.Combine(d, "user.js"));
            actions.Add(
                "• Write user.js in your Firefox profile(s) to force-enable accessibility:\n" +
                string.Join("\n", profiles.Select(p => "    " + p)) + "\n" +
                "  Appends user_pref(\"accessibility.force_disabled\", -1); — Firefox reads user.js\n" +
                "  at every startup as the override file and never writes back to it.");
        }

        return actions;
    }

    public Task<SetupResult> RemoveAsync(CancellationToken ct)
    {
        try
        {
            var removedEnv = TryRemoveOwnedFile(EnvFilePath());
            var removedLaunchers = RemoveOwnedLaunchers();
            var cleanedProfiles = RemoveOwnedFirefoxAccessibilityEntries();

            var summary = new StringBuilder("Browser accessibility integration removed.");
            if (!removedEnv)
                summary.Append(" Left env file in place (not owned by TypeWhisper).");
            if (removedLaunchers.Count > 0)
            {
                summary.Append(' ');
                summary.Append("Removed launchers: ");
                summary.Append(string.Join(", ", removedLaunchers));
                summary.Append('.');
            }
            if (cleanedProfiles.Count > 0)
            {
                summary.Append(' ');
                summary.Append("Cleaned Firefox profile(s): ");
                summary.Append(string.Join(", ", cleanedProfiles));
                summary.Append('.');
            }

            return Task.FromResult(new SetupResult(true, summary.ToString()));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new SetupResult(false,
                "Could not remove browser accessibility integration.",
                ex.Message));
        }
    }

    /// <summary>
    /// True when there is at least one piece of integration we installed —
    /// env file, patched launcher, or Firefox user.js entry we own. Drives
    /// whether the Profiles UI shows a Revert button. We never count
    /// Firefox prefs.js entries here: those might have been set by the
    /// user via about:config and aren't ours to remove.
    /// </summary>
    public bool HasInstalledChanges()
    {
        if (File.Exists(EnvFilePath()) && FileStartsWithOwnershipMarker(EnvFilePath()))
            return true;
        if (HasOwnedLauncher(FirefoxLauncherNames)) return true;
        if (HasOwnedLauncher(ChromiumLauncherNames)) return true;
        return EnumerateFirefoxProfileDirs()
            .Any(dir => UserJsHasOwnedAccessibilityEntry(Path.Combine(dir, "user.js")));
    }

    /// <summary>
    /// Itemizes what <see cref="RemoveAsync"/> would actually remove right
    /// now. The Profiles UI feeds this into a confirmation dialog before
    /// the revert runs, so the user sees every file path that will be
    /// touched, including which Firefox profile(s) will lose the
    /// accessibility override.
    /// </summary>
    public IReadOnlyList<string> DescribeRevertActions()
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
                var backupExists = File.Exists(Path.Combine(LauncherBackupDir(), name));
                sb.Append("    ").Append(path);
                sb.Append(backupExists ? "  (restore from backup)" : "  (delete)");
                sb.Append('\n');
            }
            actions.Add(sb.ToString().TrimEnd('\n'));
        }

        var profilesWithOwnership = EnumerateFirefoxProfileDirs()
            .Where(dir => UserJsHasOwnedAccessibilityEntry(Path.Combine(dir, "user.js")))
            .Select(dir => Path.Combine(dir, "user.js"))
            .ToList();
        if (profilesWithOwnership.Count > 0)
        {
            actions.Add(
                "• Remove the TypeWhisper accessibility override line from user.js in:\n"
                + string.Join("\n", profilesWithOwnership.Select(p => "    " + p))
                + "\n  (delete the file if it becomes empty)");
        }

        return actions;
    }

    private static IEnumerable<string> EnumerateOwnedLauncherPaths()
    {
        var dir = UserApplicationsDir();
        if (!Directory.Exists(dir)) yield break;
        foreach (var name in FirefoxLauncherNames.Concat(ChromiumLauncherNames))
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path) && FileStartsWithOwnershipMarker(path))
                yield return path;
        }
    }

    private const string UserJsOwnershipMarker = "// Set by TypeWhisper";

    private static bool UserJsHasOwnedAccessibilityEntry(string userJsPath)
    {
        if (!File.Exists(userJsPath)) return false;
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

    private static IReadOnlyList<string> RemoveOwnedFirefoxAccessibilityEntries()
    {
        var cleaned = new List<string>();
        foreach (var profileDir in EnumerateFirefoxProfileDirs())
        {
            var userJsPath = Path.Combine(profileDir, "user.js");
            if (!UserJsHasOwnedAccessibilityEntry(userJsPath)) continue;

            try
            {
                var content = File.ReadAllText(userJsPath);
                // Strip the attribution comment plus the following pref line
                // in a single match. The user's other prefs (if any) stay
                // untouched, including a manually-added force_disabled that
                // happens to share the value — we only remove the pair we
                // wrote ourselves, identified by the comment marker.
                var pattern =
                    @"^//\s*Set by TypeWhisper[^\r\n]*\r?\n" +
                    @"user_pref\(\s*""accessibility\.force_disabled""\s*,\s*-1\s*\)\s*;\s*\r?\n?";
                var stripped = Regex.Replace(content, pattern, "", RegexOptions.Multiline);

                if (stripped == content) continue;

                if (string.IsNullOrWhiteSpace(stripped))
                {
                    File.Delete(userJsPath);
                }
                else
                {
                    var tmp = userJsPath + ".tmp";
                    File.WriteAllText(tmp, stripped);
                    File.Move(tmp, userJsPath, overwrite: true);
                }
                cleaned.Add(Path.GetFileName(profileDir));
            }
            catch
            {
                // Skip profiles we can't write to; the summary will note
                // what we managed to clean up.
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
        File.Move(tempPath, path, overwrite: true);
    }

    private static IReadOnlyList<string> PatchLaunchers(IReadOnlyList<string> names, Func<string, string> transformContent)
    {
        var userAppsDir = UserApplicationsDir();
        Directory.CreateDirectory(userAppsDir);

        var patched = new List<string>();

        foreach (var name in names)
        {
            var userCopy = Path.Combine(userAppsDir, name);
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
                    continue;
            }
            else
            {
                var systemSource = FindSystemLauncher(name);
                if (systemSource is null)
                    continue;

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
            File.Move(tempPath, userCopy, overwrite: true);
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
            var backupPath = Path.Combine(backupDir, name);
            // Preserve the oldest backup if we ran setup multiple times.
            if (!File.Exists(backupPath))
                File.Copy(userCopy, backupPath, overwrite: false);
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
                continue;
            if (line.Contains(flag, StringComparison.Ordinal))
                continue;

            lines[i] = InsertChromiumFlag(line, flag);
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Prepends the Firefox-family env wrapper to every <c>Exec=</c>
    /// line in the .desktop content. Inlining the env vars on the
    /// launcher means accessibility takes effect on every menu launch
    /// without depending on systemd-user reading
    /// <c>~/.config/environment.d/</c> — which can silently fail to
    /// happen across logouts on some session managers.
    /// </summary>
    internal static string PrependEnvWrapperToExecLines(string content)
    {
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith("Exec=", StringComparison.Ordinal))
                continue;
            if (line.Contains("MOZ_ENABLE_ACCESSIBILITY=", StringComparison.Ordinal))
                continue;

            const int prefixEnd = 5; // "Exec=".Length
            lines[i] = "Exec=" + FirefoxEnvWrapper + " " + line[prefixEnd..];
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Inserts <paramref name="flag"/> into an <c>Exec=</c> line at the
    /// position the browser actually receives it.
    /// Naively inserting after the first token breaks Flatpak launchers
    /// (<c>Exec=/usr/bin/flatpak run org.chromium.Chromium %U</c>) and
    /// env-wrappers (<c>Exec=env VAR=x /usr/bin/chrome %U</c>) — in those
    /// cases the wrapper would consume or reject the flag. Anchoring on the
    /// XDG field-code (<c>%U</c>, <c>%F</c>, ...) or Flatpak escape marker
    /// (<c>@@</c>) puts the flag in the browser's argument position for
    /// both wrapped and unwrapped launchers. Falls back to appending when
    /// the Exec line has no field codes (rare).
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
            leftEnd--;

        return execLine[..leftEnd] + " " + flag + " " + execLine[tailStart..];
    }

    private static int FindFieldCodeOrFlatpakEscape(string line, int searchStart)
    {
        for (var i = searchStart; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '%' && i + 1 < line.Length)
            {
                var next = line[i + 1];
                // %% is an XDG-escaped literal percent — skip both chars so
                // it doesn't masquerade as a real field code like %f.
                if (next == '%')
                {
                    i++;
                    continue;
                }
                if (char.IsLetterOrDigit(next))
                    return i;
            }
            else if (c == '@' && i + 1 < line.Length && line[i + 1] == '@')
            {
                return i;
            }
        }
        return -1;
    }

    private static string? FindSystemLauncher(string name)
    {
        foreach (var dir in SystemLauncherDirectories)
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static bool HasOwnedLauncher(IReadOnlyList<string> launcherNames)
    {
        var dir = UserApplicationsDir();
        if (!Directory.Exists(dir))
            return false;

        foreach (var name in launcherNames)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path) && FileStartsWithOwnershipMarker(path))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when, for every launcher in <paramref name="launcherNames"/>
    /// that's actually installed on this system, our patched shadow
    /// exists in the user applications directory. Used by
    /// <see cref="IsCurrentlyConfigured"/> to decide whether a browser
    /// family is fully covered — distinct from <see cref="HasOwnedLauncher"/>,
    /// which only checks whether we've patched *any* launcher in the
    /// family and is used by <see cref="HasInstalledChanges"/> to decide
    /// whether a revert has anything to do. Returns true when nothing in
    /// the family is installed (vacuously satisfied).
    /// </summary>
    private static bool AllInstalledLaunchersOwned(IReadOnlyList<string> launcherNames)
    {
        var userDir = UserApplicationsDir();
        foreach (var name in launcherNames)
        {
            var isInstalled = FindSystemLauncher(name) is not null
                || HasUserOwnedOrNonOwnedLauncher(userDir, name);
            if (!isInstalled) continue;

            var ownedShadow = Path.Combine(userDir, name);
            if (!File.Exists(ownedShadow) || !FileStartsWithOwnershipMarker(ownedShadow))
                return false;
        }
        return true;
    }

    private static bool HasUserOwnedOrNonOwnedLauncher(string userDir, string name)
    {
        var path = Path.Combine(userDir, name);
        return File.Exists(path);
    }

    private static bool HasInstalledLauncher(IReadOnlyList<string> launcherNames)
    {
        var userDir = UserApplicationsDir();
        foreach (var name in launcherNames)
        {
            // User-local launcher counts as "installed" only if it's not
            // one of our patched shadows (otherwise an env-file-only user
            // looks "installed" purely because we patched their launcher).
            var userPath = Path.Combine(userDir, name);
            if (File.Exists(userPath) && !FileStartsWithOwnershipMarker(userPath))
                return true;
            if (FindSystemLauncher(name) is not null)
                return true;
        }
        return false;
    }

    private static IReadOnlyList<string> RemoveOwnedLaunchers()
    {
        var dir = UserApplicationsDir();
        if (!Directory.Exists(dir))
            return [];

        var backupDir = LauncherBackupDir();
        var removed = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.desktop"))
        {
            if (!FileStartsWithOwnershipMarker(file))
                continue;

            var name = Path.GetFileName(file);
            var backupPath = Path.Combine(backupDir, name);

            try
            {
                if (File.Exists(backupPath))
                {
                    // Atomic restore: write to .tmp then move.
                    var tempPath = file + ".restore.tmp";
                    File.Copy(backupPath, tempPath, overwrite: true);
                    File.Move(tempPath, file, overwrite: true);
                    try { File.Delete(backupPath); } catch { }
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
                // Best effort — leave files we can't process and report what we managed to clean.
            }
        }

        try
        {
            if (Directory.Exists(backupDir) && !Directory.EnumerateFileSystemEntries(backupDir).Any())
                Directory.Delete(backupDir);
        }
        catch { }

        return removed;
    }

    private static bool TryRemoveOwnedFile(string path)
    {
        if (!File.Exists(path))
            return true;
        if (!FileStartsWithOwnershipMarker(path))
            return false;
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
        return Path.Combine(home, ".config", "environment.d", EnvFileName);
    }

    private static string UserApplicationsDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", "applications");
    }

    private static string LauncherBackupDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", "typewhisper", "launcher-backups");
    }
}
