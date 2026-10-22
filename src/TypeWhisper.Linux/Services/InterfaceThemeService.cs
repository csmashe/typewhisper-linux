using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Applies the saved <see cref="AppSettings.InterfaceTheme" /> to the application and
///     re-applies it whenever settings change, so open windows switch without a restart.
/// </summary>
public sealed class InterfaceThemeService(ISettingsService settings)
{
    private Application? _application;

    /// <summary>Applies the saved theme now and follows later changes. Call on the UI thread.</summary>
    public void Start(Application application)
    {
        _application = application;
        Apply(settings.Current.InterfaceTheme);
        settings.SettingsChanged += OnSettingsChanged;
    }

    public static ThemeVariant ToThemeVariant(InterfaceTheme theme)
    {
        return theme switch
        {
            InterfaceTheme.System => ThemeVariant.Default,
            InterfaceTheme.Light => ThemeVariant.Light,
            _ => ThemeVariant.Dark,
        };
    }

    private void OnSettingsChanged(AppSettings _)
    {
        // Saves can come from any thread; read Current when the post runs so queued changes
        // coalesce onto the newest commit.
        Dispatcher.UIThread.Post(() => Apply(settings.Current.InterfaceTheme));
    }

    private void Apply(InterfaceTheme theme)
    {
        var variant = ToThemeVariant(theme);
        if (_application is not null && _application.RequestedThemeVariant != variant)
        {
            _application.RequestedThemeVariant = variant;
        }
    }
}
