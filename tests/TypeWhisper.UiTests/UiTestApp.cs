using Avalonia;
using Avalonia.Dialogs;
using Avalonia.Headless;
using TypeWhisper.Linux;

namespace TypeWhisper.UiTests;

/// <summary>
///     The production <see cref="App" /> styles and theme, without its desktop bootstrap
///     (tray, hotkeys, plugins, main window). Tests build their own service graph.
/// </summary>
public sealed class UiTestApp : App
{
#pragma warning disable CA1416 // Linux-only test project.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<UiTestApp>()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .UseManagedSystemDialogs()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
#pragma warning restore CA1416

    public override void OnFrameworkInitializationCompleted()
    {
    }
}
