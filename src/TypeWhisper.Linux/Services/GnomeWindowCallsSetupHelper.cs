using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Probes for the "Window Calls" GNOME Shell extension and opens its install page.
///     Installation is browser-only — extensions.gnome.org requires the GNOME Browser
///     Integration extension and a user click; we can't install it ourselves.
/// </summary>
public sealed class GnomeWindowCallsSetupHelper
{
    private readonly IProcessRunner _processRunner;

    public GnomeWindowCallsSetupHelper()
        : this(new ProcessRunner()) { }

    public GnomeWindowCallsSetupHelper(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    private const string ExtensionInstallUrl =
        "https://extensions.gnome.org/extension/4974/window-calls/";

    private const string DBusDest = "org.gnome.Shell";

    // Accept either the original "Window Calls" or the "Window Calls Extended"
    // fork — they expose a compatible List method at different paths.
    private static readonly (string Path, string Interface)[] s_endpoints =
    [
        ("/org/gnome/Shell/Extensions/Windows", "org.gnome.Shell.Extensions.Windows"),
        ("/org/gnome/Shell/Extensions/WindowsExt", "org.gnome.Shell.Extensions.WindowsExt"),
    ];

    // kept instance: injected as a DI/test seam by callers
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "kept instance: injected as a DI/test seam")]
    // ReSharper disable once MemberCanBeMadeStatic.Global
    public bool IsApplicable()
    {
        var raw = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var lower = raw.ToLowerInvariant();
        return lower.Contains("gnome") || lower.Contains("ubuntu");
    }

    // kept instance: injected as a DI/test seam by callers
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "kept instance: injected as a DI/test seam")]
    // ReSharper disable once MemberCanBeMadeStatic.Global
    public bool IsCurrentlyInstalled()
    {
        if (!DesktopDetector.BinaryExists("gdbus"))
        {
            return false;
        }

        // Don't use `gdbus introspect`: org.gnome.Shell answers it on any path
        // (empty node), giving a false positive. Actually CALL List — a missing
        // object/method exits non-zero. Try each known endpoint.
        foreach (var (path, iface) in s_endpoints)
        {
            var result = _processRunner.RunProbe(
                new ProcessCommand(
                    "gdbus",
                    [
                        "call",
                        "--session",
                        "--dest",
                        DBusDest,
                        "--object-path",
                        path,
                        "--method",
                        $"{iface}.List",
                    ]
                ),
                new ProcessOneShotOptions(
                    Timeout: TimeSpan.FromSeconds(1),
                    StandardOutput: ProcessCaptureMode.Discard,
                    StandardError: ProcessCaptureMode.Discard
                )
            );
            if (result is { Status: ProcessRunStatus.Exited, ExitCode: 0 })
            {
                return true;
            }
        }

        return false;
    }

    // kept instance: injected as a DI/test seam by callers
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "kept instance: injected as a DI/test seam")]
    // ReSharper disable once MemberCanBeMadeStatic.Global
    public bool TryOpenInstallPage()
    {
        return _processRunner.LaunchUri(new Uri(ExtensionInstallUrl)).Started;
    }
}
