using Avalonia;
using Avalonia.Headless;
using TypeWhisper.Linux;

namespace TypeWhisper.UiTests;

/// <summary>
///     The production <see cref="App" /> styles and theme, without its desktop bootstrap
///     (tray, hotkeys, plugins, main window). Tests build their own service graph.
/// </summary>
public sealed class UiTestApp : App
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<UiTestApp>()
            .UseSkia()
            .UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public override void OnFrameworkInitializationCompleted()
    {
    }
}
