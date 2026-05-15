using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// Linux tray icon via Avalonia's built-in TrayIcon (StatusNotifierItem on
/// desktops with libappindicator support — GNOME needs the AppIndicator
/// extension; KDE/XFCE/Budgie/Cinnamon/Unity all support SNI natively).
///
/// Headless / non-SNI desktops will silently skip — the tray is optional.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly IProcessRunner _runner;
    private TrayIcon? _trayIcon;
    private TrayIcons? _trayIcons;
    private bool _disposed;

    public event EventHandler? ShowSettingsRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? DictationToggleRequested;

    public TrayIconService(IProcessRunner runner)
    {
        _runner = runner;
    }

    /// <summary>
    /// Whether a usable system tray was detected at <see cref="Initialize"/>
    /// time. Consulted before the close button hides the window — see
    /// backlog #18: hiding with no tray strands the user with no UI.
    /// </summary>
    public bool IsTrayAvailable { get; private set; }

    public void Initialize()
    {
        // A StatusNotifier host existing (the D-Bus probe) is necessary but
        // not sufficient — IsTrayAvailable must also mean our icon actually
        // registered. Set it true only after SetIcons succeeds; any failure
        // (no host, TrayIcon/SetIcons throwing, no Application.Current) leaves
        // it false so close-to-tray won't hide the window into a tray entry
        // that isn't there (backlog #18).
        var hostPresent = ProbeTrayAvailable();

        try
        {
            _trayIcon = new TrayIcon
            {
                ToolTipText = "TypeWhisper",
                IsVisible = true,
                Menu = BuildMenu(),
                Icon = LoadIcon(),
            };
            _trayIcon.Clicked += (_, _) => ShowSettingsRequested?.Invoke(this, EventArgs.Empty);

            if (Application.Current is { } app)
            {
                _trayIcons = new TrayIcons { _trayIcon };
                TrayIcon.SetIcons(app, _trayIcons);
                IsTrayAvailable = hostPresent;
            }
        }
        catch (Exception ex)
        {
            IsTrayAvailable = false;
            Debug.WriteLine($"[TrayIconService] Tray init failed: {ex.Message}");
        }
    }

    /// <summary>
    /// True when a StatusNotifierItem host (system tray) is present on the
    /// session bus. Avalonia's <see cref="TrayIcon"/> silently no-ops when
    /// there is no host — it never throws or otherwise reports failure — so
    /// a successful <see cref="Initialize"/> proves nothing. We read the
    /// StatusNotifierWatcher's <c>IsStatusNotifierHostRegistered</c> property
    /// over D-Bus: it is true only when a watcher exists <em>and</em> a host
    /// has registered with it (KDE's panel, GNOME's AppIndicator extension,
    /// waybar's tray module — all qualify). Checking the watcher's mere name
    /// ownership instead would mis-report a stale watcher left behind with no
    /// host. Fails safe: any probe error (gdbus missing, bus unreachable, no
    /// watcher at all) counts as "no tray" so close-to-tray falls back to
    /// quitting rather than stranding the user — backlog #18.
    /// </summary>
    internal bool ProbeTrayAvailable()
    {
        // gdbus ships with glib2 — present on every Linux desktop. One
        // property read covers every case: no watcher → gdbus errors →
        // false; a watcher with no host → "(<false>,)" → false; a watcher
        // with a registered host → "(<true>,)" → true.
        var result = _runner.RunAsync(
            "gdbus",
            new[]
            {
                "call", "--session",
                "--dest", "org.kde.StatusNotifierWatcher",
                "--object-path", "/StatusNotifierWatcher",
                "--method", "org.freedesktop.DBus.Properties.Get",
                "org.kde.StatusNotifierWatcher",
                "IsStatusNotifierHostRegistered",
            },
            timeout: TimeSpan.FromSeconds(2))
            .GetAwaiter().GetResult();

        // gdbus prints the bool variant as "(<true>,)" / "(<false>,)";
        // a non-zero exit (no watcher, gdbus missing) is treated as no tray.
        return result.Succeeded
            && result.StandardOutput.Contains("true", StringComparison.Ordinal);
    }

    public void UpdateTooltip(string text)
    {
        if (_trayIcon is not null)
            _trayIcon.ToolTipText = text;
    }

    private static WindowIcon? LoadIcon()
    {
        // Prefer 32x32 for tray — most SNI implementations downscale from
        // there cleanly. Fall back to the .ico (Avalonia decodes multi-size
        // .ico files natively) if the PNG isn't where we expect it.
        var baseDir = AppContext.BaseDirectory;
        var png = Path.Combine(baseDir, "Resources", "typewhisper-32.png");
        var ico = Path.Combine(baseDir, "Resources", "typewhisper.ico");

        try
        {
            if (File.Exists(png)) return new WindowIcon(png);
            if (File.Exists(ico)) return new WindowIcon(ico);

            // Last-resort: try the Avalonia resource URI embedded in the
            // assembly itself, useful when running from a single-file publish.
            return new WindowIcon(AssetLoader.Open(new Uri("avares://typewhisper/Resources/typewhisper-32.png")));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TrayIconService] Icon load failed: {ex.Message}");
            return null;
        }
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();

        var dictate = new NativeMenuItem("Toggle Dictation");
        dictate.Click += (_, _) => DictationToggleRequested?.Invoke(this, EventArgs.Empty);

        var settings = new NativeMenuItem("Settings…");
        settings.Click += (_, _) => ShowSettingsRequested?.Invoke(this, EventArgs.Empty);

        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        menu.Add(dictate);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(settings);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(exit);

        return menu;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Application.Current is { } app)
            TrayIcon.SetIcons(app, null);
        _trayIcons?.Clear();
        _trayIcon?.Dispose();
    }
}
