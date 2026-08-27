using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services;

internal enum BrowserEngineFamily
{
    Firefox,
    Chromium,
}

internal enum BrowserLauncherPatchMode
{
    None,
    FirefoxEnvironment,
    ChromiumRendererAccessibility,
}

[Flags]
internal enum BrowserCapabilities
{
    ActiveWindowDetection = 1,
    AtSpiExtraction = 2,
    InteractiveX11Capture = 4,
    LauncherSetup = 8,
    FirefoxProfileSetup = 16,
    TitleOnlyDetection = 32,
}

internal enum ProfileRootBase
{
    Home,
    XdgConfigHome,
}

internal sealed record BrowserProfileRoot(
    ProfileRootBase Base,
    string RelativePath
);

internal sealed record BrowserDescriptor(
    string Id,
    string CanonicalProcessName,
    BrowserEngineFamily EngineFamily,
    IReadOnlyList<string> ProcessAliases,
    IReadOnlyList<string> WindowIdentityAliases,
    IReadOnlyList<string> TitleAliases,
    IReadOnlyList<string> DesktopIds,
    IReadOnlyList<BrowserProfileRoot> ProfileRoots,
    BrowserLauncherPatchMode LauncherPatchMode,
    BrowserCapabilities Capabilities
)
{
    internal bool HasCapability(BrowserCapabilities capability)
    {
        return (Capabilities & capability) == capability;
    }
}

/// <summary>
///     Single source of Linux browser identity and accessibility-setup support.
///     Consumers project only the aliases and capabilities relevant to their job;
///     desktop IDs are intentionally never treated as process aliases.
/// </summary>
internal static class BrowserDescriptorCatalog
{
    /// <summary>Shared by the descriptor below and the callers that special-case Zen.</summary>
    internal const string ZenId = "zen";

    private const BrowserCapabilities DetectionAndSetup =
        BrowserCapabilities.ActiveWindowDetection
        | BrowserCapabilities.AtSpiExtraction
        | BrowserCapabilities.InteractiveX11Capture
        | BrowserCapabilities.LauncherSetup;

    private const BrowserCapabilities FirefoxDetectionAndSetup =
        DetectionAndSetup | BrowserCapabilities.FirefoxProfileSetup;

    private static readonly IReadOnlyList<BrowserDescriptor> s_descriptors =
    [
        new(
            "firefox",
            "firefox",
            BrowserEngineFamily.Firefox,
            ["firefox"],
            ["Firefox", "Mozilla Firefox", "org.mozilla.firefox"],
            [],
            ["firefox.desktop", "org.mozilla.firefox.desktop", "firefox-esr.desktop"],
            [
                new BrowserProfileRoot(ProfileRootBase.Home, ".mozilla/firefox"),
                new BrowserProfileRoot(ProfileRootBase.XdgConfigHome, "mozilla/firefox"),
                new BrowserProfileRoot(
                    ProfileRootBase.Home,
                    "snap/firefox/common/.mozilla/firefox"
                ),
                new BrowserProfileRoot(
                    ProfileRootBase.Home,
                    ".var/app/org.mozilla.firefox/.mozilla/firefox"
                ),
            ],
            BrowserLauncherPatchMode.FirefoxEnvironment,
            FirefoxDetectionAndSetup
        ),
        new(
            "librewolf",
            "librewolf",
            BrowserEngineFamily.Firefox,
            ["librewolf"],
            ["LibreWolf", "io.gitlab.librewolf-community"],
            [],
            ["librewolf.desktop", "io.gitlab.librewolf-community.desktop"],
            [
                new BrowserProfileRoot(
                    ProfileRootBase.Home,
                    ".var/app/io.gitlab.librewolf-community/.librewolf"
                ),
                new BrowserProfileRoot(ProfileRootBase.Home, ".librewolf"),
                new BrowserProfileRoot(
                    ProfileRootBase.XdgConfigHome,
                    "librewolf/librewolf"
                ),
            ],
            BrowserLauncherPatchMode.FirefoxEnvironment,
            FirefoxDetectionAndSetup
        ),
        new(
            "waterfox",
            "waterfox",
            BrowserEngineFamily.Firefox,
            ["waterfox"],
            ["Waterfox", "net.waterfox.waterfox"],
            [],
            ["waterfox.desktop", "net.waterfox.waterfox.desktop"],
            [
                new BrowserProfileRoot(ProfileRootBase.Home, ".waterfox"),
                new BrowserProfileRoot(
                    ProfileRootBase.Home,
                    ".var/app/net.waterfox.waterfox/.waterfox"
                ),
            ],
            BrowserLauncherPatchMode.FirefoxEnvironment,
            FirefoxDetectionAndSetup
        ),
        new(
            ZenId,
            "zen",
            BrowserEngineFamily.Firefox,
            ["zen", "zen-browser", "zen-bin"],
            [
                "Zen",
                "Zen Browser",
                "zen-browser",
                "zen-bin",
                "app.zen_browser.zen",
                "io.github.zen_browser.zen",
            ],
            ["Zen Browser"],
            [
                "zen.desktop",
                "app.zen_browser.zen.desktop",
                "io.github.zen_browser.zen.desktop",
            ],
            [
                new BrowserProfileRoot(
                    ProfileRootBase.Home,
                    ".var/app/app.zen_browser.zen/.zen"
                ),
                new BrowserProfileRoot(
                    ProfileRootBase.Home,
                    ".var/app/io.github.zen_browser.zen/.zen"
                ),
                new BrowserProfileRoot(ProfileRootBase.Home, ".zen"),
            ],
            BrowserLauncherPatchMode.FirefoxEnvironment,
            FirefoxDetectionAndSetup | BrowserCapabilities.TitleOnlyDetection
        ),
        new(
            "chrome",
            "chrome",
            BrowserEngineFamily.Chromium,
            ["chrome"],
            ["Chrome", "Google Chrome", "com.google.Chrome"],
            [],
            ["google-chrome.desktop", "com.google.Chrome.desktop"],
            [],
            BrowserLauncherPatchMode.ChromiumRendererAccessibility,
            DetectionAndSetup
        ),
        new(
            "chromium",
            "chromium",
            BrowserEngineFamily.Chromium,
            ["chromium"],
            ["Chromium", "org.chromium.Chromium"],
            [],
            [
                "chromium.desktop",
                "chromium-browser.desktop",
                "org.chromium.Chromium.desktop",
            ],
            [],
            BrowserLauncherPatchMode.ChromiumRendererAccessibility,
            DetectionAndSetup
        ),
        new(
            "edge",
            "msedge",
            BrowserEngineFamily.Chromium,
            ["msedge", "edge"],
            ["Edge", "msedge", "Microsoft Edge", "com.microsoft.Edge"],
            [],
            ["microsoft-edge.desktop", "com.microsoft.Edge.desktop"],
            [],
            BrowserLauncherPatchMode.ChromiumRendererAccessibility,
            DetectionAndSetup
        ),
        new(
            "brave",
            "brave",
            BrowserEngineFamily.Chromium,
            ["brave"],
            ["Brave", "com.brave.Browser"],
            [],
            ["brave-browser.desktop", "com.brave.Browser.desktop"],
            [],
            BrowserLauncherPatchMode.ChromiumRendererAccessibility,
            DetectionAndSetup
        ),
        new(
            "vivaldi",
            "vivaldi",
            BrowserEngineFamily.Chromium,
            ["vivaldi"],
            ["Vivaldi", "com.vivaldi.Vivaldi"],
            [],
            ["vivaldi-stable.desktop", "com.vivaldi.Vivaldi.desktop"],
            [],
            BrowserLauncherPatchMode.ChromiumRendererAccessibility,
            DetectionAndSetup
        ),
        new(
            "opera",
            "opera",
            BrowserEngineFamily.Chromium,
            ["opera"],
            ["Opera", "com.opera.Opera"],
            [],
            ["opera.desktop", "com.opera.Opera.desktop"],
            [],
            BrowserLauncherPatchMode.ChromiumRendererAccessibility,
            DetectionAndSetup
        ),
    ];

    private static readonly IReadOnlyDictionary<string, BrowserDescriptor> s_processAliases =
        BuildAliasIndex(s_descriptors, descriptor => descriptor.ProcessAliases);

    private static readonly IReadOnlyDictionary<string, BrowserDescriptor> s_windowAliases =
        BuildAliasIndex(s_descriptors, descriptor => descriptor.WindowIdentityAliases);

    // GNOME Shell reports the desktop-file id ("org.mozilla.firefox.desktop") as a window's
    // app ID, so desktop IDs must resolve as window identities. They stay out of the process
    // alias table: no process is ever named "firefox.desktop".
    private static readonly IReadOnlyDictionary<string, BrowserDescriptor> s_desktopIdAliases =
        BuildAliasIndex(s_descriptors, descriptor => descriptor.DesktopIds);

    static BrowserDescriptorCatalog()
    {
        ValidateDescriptors();
    }

    // ReSharper disable once ConvertToAutoPropertyWhenPossible -- s_descriptors is the storage the
    // alias-index initializers and every internal query read directly, and it stays symmetric with
    // the s_*Aliases tables; folding it into this exposed accessor would hide that ordering.
    internal static IReadOnlyList<BrowserDescriptor> All => s_descriptors;

    internal static BrowserDescriptor? ResolveProcessAlias(
        string? processName,
        BrowserCapabilities requiredCapability
    )
    {
        return ResolveExactAlias(s_processAliases, processName, requiredCapability);
    }

    internal static BrowserDescriptor? ResolveWindowIdentity(
        string? identity,
        BrowserCapabilities requiredCapability
    )
    {
        return ResolveExactAlias(s_windowAliases, identity, requiredCapability)
               ?? ResolveExactAlias(s_desktopIdAliases, identity, requiredCapability);
    }

    internal static BrowserDescriptor? ResolveTitle(
        string? title,
        BrowserCapabilities requiredCapability
    )
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        return s_descriptors.FirstOrDefault(descriptor =>
            descriptor.HasCapability(requiredCapability)
            && descriptor.HasCapability(BrowserCapabilities.TitleOnlyDetection)
            && descriptor.TitleAliases.Any(alias =>
                title.Contains(alias, StringComparison.OrdinalIgnoreCase)
            )
        );
    }

    internal static BrowserDescriptor? ResolveSnapshot(
        ActiveWindowSnapshot? snapshot,
        BrowserCapabilities requiredCapability
    )
    {
        if (snapshot is null)
        {
            return null;
        }

        return Resolve(
            snapshot.ProcessName,
            snapshot.AppId,
            snapshot.Title,
            requiredCapability
        );
    }

    internal static BrowserDescriptor? Resolve(
        string? processName,
        string? windowIdentity,
        string? title,
        BrowserCapabilities requiredCapability
    )
    {
        var resolved = ResolveProcessAlias(processName, requiredCapability)
                       ?? ResolveWindowIdentity(windowIdentity, requiredCapability);
        if (resolved is not null)
        {
            return resolved;
        }

        // Title is a last resort for windows that report no process at all (Flatpak/XWayland
        // surfaces). An observed process name outranks it even when uncatalogued, so an editor
        // showing a page titled "… — Zen Browser" is never mistaken for the browser itself.
        return string.IsNullOrWhiteSpace(processName)
            ? ResolveTitle(title, requiredCapability)
            : null;
    }

    internal static IReadOnlyList<string> GetDesktopIds(BrowserLauncherPatchMode patchMode)
    {
        return s_descriptors
            .Where(descriptor =>
                descriptor.HasCapability(BrowserCapabilities.LauncherSetup)
                && descriptor.LauncherPatchMode == patchMode
            )
            .SelectMany(descriptor => descriptor.DesktopIds)
            .ToArray();
    }

    internal static IReadOnlyList<string> GetExpandedProfileRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(xdgConfigHome))
        {
            xdgConfigHome = Path.Join(home, ".config");
        }

        return s_descriptors
            .Where(descriptor =>
                descriptor.HasCapability(BrowserCapabilities.FirefoxProfileSetup)
            )
            .SelectMany(descriptor => descriptor.ProfileRoots)
            .Select(root =>
                Path.Join(
                    root.Base == ProfileRootBase.Home ? home : xdgConfigHome,
                    root.RelativePath
                )
            )
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static BrowserDescriptor? ResolveExactAlias(
        IReadOnlyDictionary<string, BrowserDescriptor> aliases,
        string? value,
        BrowserCapabilities requiredCapability
    )
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return aliases.TryGetValue(value.Trim(), out var descriptor)
               && descriptor.HasCapability(requiredCapability)
            ? descriptor
            : null;
    }

    private static Dictionary<string, BrowserDescriptor> BuildAliasIndex(
        IEnumerable<BrowserDescriptor> descriptors,
        Func<BrowserDescriptor, IReadOnlyList<string>> selectAliases
    )
    {
        var result = new Dictionary<string, BrowserDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in descriptors)
        {
            foreach (var alias in selectAliases(descriptor))
            {
                if (!result.TryAdd(alias, descriptor))
                {
                    throw new InvalidOperationException($"Duplicate browser alias: {alias}");
                }
            }
        }

        return result;
    }

    private static void ValidateDescriptors()
    {
        ValidateUnique("descriptor ID", s_descriptors.Select(descriptor => descriptor.Id));
        ValidateUnique(
            "process alias",
            s_descriptors.SelectMany(descriptor => descriptor.ProcessAliases)
        );
        ValidateUnique(
            "window identity alias",
            s_descriptors.SelectMany(descriptor => descriptor.WindowIdentityAliases)
        );
        ValidateUnique(
            "title alias",
            s_descriptors.SelectMany(descriptor => descriptor.TitleAliases)
        );
        ValidateUnique(
            "desktop ID",
            s_descriptors.SelectMany(descriptor => descriptor.DesktopIds)
        );

        foreach (var descriptor in s_descriptors)
        {
            if (
                descriptor.HasCapability(BrowserCapabilities.ActiveWindowDetection)
                && descriptor.ProcessAliases.Count == 0
                && descriptor.WindowIdentityAliases.Count == 0
            )
            {
                throw new InvalidOperationException(
                    $"Browser '{descriptor.Id}' has detection capability but no identity alias."
                );
            }

            var launcherProjectionIsComplete =
                descriptor.LauncherPatchMode != BrowserLauncherPatchMode.None
                && descriptor.DesktopIds.Count > 0;
            if (
                descriptor.HasCapability(BrowserCapabilities.LauncherSetup)
                != launcherProjectionIsComplete
            )
            {
                throw new InvalidOperationException(
                    $"Browser '{descriptor.Id}' has inconsistent launcher setup metadata."
                );
            }

            var profileProjectionIsComplete = descriptor.ProfileRoots.Count > 0;
            if (
                descriptor.HasCapability(BrowserCapabilities.FirefoxProfileSetup)
                != profileProjectionIsComplete
            )
            {
                throw new InvalidOperationException(
                    $"Browser '{descriptor.Id}' has inconsistent profile setup metadata."
                );
            }

            var titleProjectionIsComplete = descriptor.TitleAliases.Count > 0;
            if (
                descriptor.HasCapability(BrowserCapabilities.TitleOnlyDetection)
                != titleProjectionIsComplete
            )
            {
                throw new InvalidOperationException(
                    $"Browser '{descriptor.Id}' has inconsistent title detection metadata."
                );
            }
        }
    }

    private static void ValidateUnique(string kind, IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
            {
                throw new InvalidOperationException($"Invalid or duplicate browser {kind}: {value}");
            }
        }
    }
}
