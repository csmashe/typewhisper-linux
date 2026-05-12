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
    private TrayIcon? _trayIcon;
    private TrayIcons? _trayIcons;
    private bool _disposed;

    public event EventHandler? ShowSettingsRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? DictationToggleRequested;

    public void Initialize()
    {
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
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TrayIconService] Tray init failed: {ex.Message}");
        }
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
