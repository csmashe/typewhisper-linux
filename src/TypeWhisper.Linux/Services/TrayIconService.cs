using System.Diagnostics;
using Avalonia.Controls;

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
            };
            _trayIcon.Clicked += (_, _) => ShowSettingsRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // Tray isn't available on every session (e.g. GNOME without AppIndicator
            // extension, headless CI). Log and continue — app stays usable.
            Debug.WriteLine($"[TrayIconService] Tray init failed: {ex.Message}");
        }
    }

    public void UpdateTooltip(string text)
    {
        if (_trayIcon is not null)
            _trayIcon.ToolTipText = text;
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
        _trayIcon?.Dispose();
    }
}
