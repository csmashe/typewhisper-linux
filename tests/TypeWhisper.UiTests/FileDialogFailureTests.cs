using System.Reflection;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Dialogs;
using Avalonia.Dialogs.Internal;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Linux.Views;
using TypeWhisper.Linux.Views.Sections;
using Xunit;

namespace TypeWhisper.UiTests;

/// <summary>
///     A picker can hand back a file the app may not read or write; the failure must reach the
///     user instead of only the trace log, and the file must stay untouched.
/// </summary>
[Collection("ui")]
[SupportedOSPlatform("linux")]
public sealed class FileDialogFailureTests(UiSession ui)
{
    public static TheoryData<string, UnixFileMode> Cases => new()
    {
        { "snippet-export", UnixFileMode.UserRead },
        { "snippet-import", UnixFileMode.None },
        { "history-export", UnixFileMode.UserRead },
    };

    [PermissionsEnforcedTheory]
    [MemberData(nameof(Cases))]
    public async Task DeniedFileAccessShowsFailureAndKeepsFile(string operation, UnixFileMode mode)
    {
        var path = Path.Join(UiEnvironment.Root, $"{operation}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "[]");
        File.SetUnixFileMode(path, mode);

        try
        {
            var message = await ui.Session.Dispatch(() => RunAndCaptureDialog(operation, path), CancellationToken.None);

            Assert.Contains("denied", message, StringComparison.OrdinalIgnoreCase);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Equal("[]", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Delete(path);
        }
    }

    private string RunAndCaptureDialog(string operation, string path)
    {
        Control section = operation.StartsWith("snippet", StringComparison.Ordinal)
            ? new SnippetsSection { DataContext = ui.Services.GetRequiredService<SnippetsSectionViewModel>() }
            : new HistorySection { DataContext = ui.Services.GetRequiredService<HistorySectionViewModel>() };
        var handler = operation.EndsWith("import", StringComparison.Ordinal) ? "OnImport" : "OnExport";

        var opened = new List<Window>();
        using var subscription = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => opened.Add(window));
        var window = new Window { Content = section };
        try
        {
            window.Show();
            section.GetType()
                .GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(section, [null, new RoutedEventArgs()]);

            PumpUntil(() => Find<ManagedFileChooser>(opened) is not null);
            var chooser = Assert.IsType<ManagedFileChooserViewModel>(Find<ManagedFileChooser>(opened)?.DataContext);
            chooser.Navigate(Path.GetDirectoryName(path)!, Path.GetFileName(path));
            chooser.FileName = Path.GetFileName(path);
            chooser.Ok();
            // Saving over an existing file asks first; confirm as the user would.
            PumpUntil(() => Find<ManagedFileChooserOverwritePrompt>(opened) is not null || opened.OfType<MessageDialogWindow>().Any());
            Find<ManagedFileChooserOverwritePrompt>(opened)?.Confirm();

            PumpUntil(() => opened.OfType<MessageDialogWindow>().Any());
            var failure = Assert.Single(opened.OfType<MessageDialogWindow>());
            var text = failure.FindControl<TextBlock>("MessageTextBlock")!.Text ?? "";
            failure.Close();
            return text;
        }
        finally
        {
            window.Close();
        }
    }

    private static T? Find<T>(IEnumerable<Window> windows)
        where T : Visual =>
        windows.SelectMany(w => w.GetVisualDescendants()).OfType<T>().FirstOrDefault(v => v.IsEffectivelyVisible);

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed class PermissionsEnforcedTheoryAttribute : TheoryAttribute
    {
        public PermissionsEnforcedTheoryAttribute()
        {
            // Root or CAP_DAC_OVERRIDE ignores file modes, so nothing would be denied.
            var probe = Path.Join(Path.GetTempPath(), $"tw-permission-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            try
            {
                File.SetUnixFileMode(probe, UnixFileMode.None);
                using (File.OpenRead(probe))
                {
                    Skip = "File permissions are not enforced for this process (root or CAP_DAC_OVERRIDE).";
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            finally
            {
                File.Delete(probe);
            }
        }
    }
}
