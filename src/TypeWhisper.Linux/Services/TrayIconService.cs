using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using System.Diagnostics;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Linux tray icon via Avalonia's built-in TrayIcon (StatusNotifierItem on
///     desktops with libappindicator support — GNOME needs the AppIndicator
///     extension; KDE/XFCE/Budgie/Cinnamon/Unity all support SNI natively).
///     Headless / non-SNI desktops will silently skip — the tray is optional.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly IProcessRunner _runner;
    private bool _disposed;
    private TrayIcon? _trayIcon;
    private TrayIcons? _trayIcons;

    public TrayIconService(IProcessRunner runner)
    {
        _runner = runner;
    }

    /// <summary>
    ///     Whether a usable system tray was confirmed at <see cref="Initialize" />
    ///     time. Hiding the window without a tray strands the user (#18).
    /// </summary>
    public bool IsTrayAvailable { get; private set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Application.Current is { } app)
        {
            TrayIcon.SetIcons(app, null);
        }

        _trayIcons?.Clear();
        _trayIcon?.Dispose();
    }

    public void Initialize()
    {
        // IsTrayAvailable is set true only after SetIcons succeeds and the host
        // probe passes — a probe alone isn't sufficient, since TrayIcon.SetIcons
        // can silently fail. Any failure leaves it false (#18).
        var hostPresent = ProbeTrayAvailable();

        try
        {
            _trayIcon = new TrayIcon
            {
                ToolTipText = "TypeWhisper", IsVisible = true, Menu = BuildMenu(), Icon = LoadIcon(),
            };
            _trayIcon.Clicked += (_, _) => ShowSettingsRequested?.Invoke(this, EventArgs.Empty);

            // ReSharper disable once InvertIf — pattern variable `app` is used in the block;
            // inverting would put it out of scope.
            if (Application.Current is { } app)
            {
                _trayIcons = [_trayIcon];
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

    // ReSharper disable once UnusedMember.Global  public API surface (dynamic tray tooltip update); not currently called in-tree
    public void UpdateTooltip(string text)
    {
        _trayIcon?.ToolTipText = text;
    }

    /// <summary>
    ///     True when a StatusNotifier host is present on the session bus.
    ///     Avalonia's TrayIcon silently no-ops when no host exists, so a
    ///     successful Initialize proves nothing. We read
    ///     <c>IsStatusNotifierHostRegistered</c> from the watcher — true only
    ///     when a host (KDE panel, GNOME AppIndicator, waybar…) has registered.
    ///     Checking mere name ownership would mis-report a stale watcher with no
    ///     host. Any probe error counts as "no tray" (fails safe, #18).
    /// </summary>
    internal bool ProbeTrayAvailable()
    {
        // gdbus (glib2, always present) prints "(<true>,)" / "(<false>,)";
        // non-zero exit (no watcher, gdbus missing) is treated as no tray.
        var result = _runner
            .RunAsync(
                "gdbus",
                [
                    "call",
                    "--session",
                    "--dest",
                    "org.kde.StatusNotifierWatcher",
                    "--object-path",
                    "/StatusNotifierWatcher",
                    "--method",
                    "org.freedesktop.DBus.Properties.Get",
                    "org.kde.StatusNotifierWatcher",
                    "IsStatusNotifierHostRegistered",
                ],
                timeout: TimeSpan.FromSeconds(2)
            )
            .GetAwaiter()
            .GetResult();

        return result.Succeeded && result.StandardOutput.Contains("true", StringComparison.Ordinal);
    }

    public event EventHandler? ShowSettingsRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? DictationToggleRequested;

    private static WindowIcon? LoadIcon()
    {
        // 32x32 PNG is preferred; most SNI hosts downscale cleanly from there.
        // Fall back to the .ico if the PNG is missing.
        var baseDir = AppContext.BaseDirectory;
        var png = Path.Join(baseDir, "Resources", "typewhisper-32.png");
        var ico = Path.Join(baseDir, "Resources", "typewhisper.ico");

        try
        {
            if (File.Exists(png))
            {
                return new WindowIcon(png);
            }

            if (File.Exists(ico))
            {
                return new WindowIcon(ico);
            }

            // Last resort: embedded Avalonia asset (single-file publish path).
            return new WindowIcon(
                AssetLoader.Open(new Uri("avares://typewhisper/Resources/typewhisper-32.png"))
            );
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

        var dictate = new NativeMenuItem(Loc.Instance["Tray.ToggleDictation"]);
        dictate.Click += (_, _) => DictationToggleRequested?.Invoke(this, EventArgs.Empty);

        var settings = new NativeMenuItem(Loc.Instance["Tray.Settings"]);
        settings.Click += (_, _) => ShowSettingsRequested?.Invoke(this, EventArgs.Empty);

        var exit = new NativeMenuItem(Loc.Instance["Tray.Exit"]);
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        menu.Add(dictate);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(settings);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(exit);

        return menu;
    }
}