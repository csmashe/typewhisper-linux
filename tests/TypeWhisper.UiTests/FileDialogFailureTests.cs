using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Dialogs;
using Avalonia.Dialogs.Internal;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Linux.Views;
using TypeWhisper.Linux.Views.Sections;
using Xunit;

namespace TypeWhisper.UiTests;

/// <summary>
///     A picker can hand back a file the app may not read or write; the failure must reach the
///     user instead of only the trace log, leave the file and data untouched, and allow a retry.
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
    public async Task DeniedFileAccessShowsFailureKeepsDataAndAllowsRetry(string operation, UnixFileMode mode)
    {
        var importing = operation.EndsWith("import", StringComparison.Ordinal);
        var snippets = ui.Services.GetRequiredService<ISnippetService>();
        var existing = new Snippet { Id = Guid.NewGuid().ToString(), Trigger = "twqa-existing", Replacement = "kept" };
        var original = importing
            ? JsonSerializer.Serialize(new List<Snippet> { new() { Id = "twqa", Trigger = "twqa-imported", Replacement = "new" } })
            : "OWNED_BEFORE";
        var path = Path.Join(UiEnvironment.Root, $"{operation}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, original);
        File.SetUnixFileMode(path, mode);
        await ui.Session.Dispatch(() => snippets.AddSnippet(existing), CancellationToken.None);

        try
        {
            var failure = await ui.Session.Dispatch(() => RunFlow(operation, path, null), CancellationToken.None);

            Assert.Contains("denied", failure, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(snippets.Snippets, s => s.Id == existing.Id);
            Assert.DoesNotContain(snippets.Snippets, s => s.Trigger == "twqa-imported");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Equal(original, await File.ReadAllTextAsync(path));

            var expected = importing ? null : await ui.Session.Dispatch(() => ExpectedExport(operation), CancellationToken.None);
            var retry = await ui.Session.Dispatch(() => RunFlow(operation, path, expected), CancellationToken.None);

            if (importing)
            {
                Assert.Equal("Imported 1 snippet(s).", retry);
                Assert.Contains(snippets.Snippets, s => s.Trigger == "twqa-imported");
            }
            else
            {
                Assert.Null(retry);
                Assert.Equal(expected, await File.ReadAllTextAsync(path));
            }
        }
        finally
        {
            await ui.Session.Dispatch(() =>
            {
                foreach (var snippet in snippets.Snippets.Where(s => s.Trigger.StartsWith("twqa-", StringComparison.Ordinal)).ToList())
                {
                    snippets.DeleteSnippet(snippet.Id);
                }
            }, CancellationToken.None);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Delete(path);
        }
    }

    private string ExpectedExport(string operation) =>
        operation.StartsWith("snippet", StringComparison.Ordinal)
            ? ui.Services.GetRequiredService<SnippetsSectionViewModel>().ExportToJson()
            : ui.Services.GetRequiredService<HistorySectionViewModel>().BuildExportContent(".json");

    /// <summary>
    ///     Picks <paramref name="path" /> in the section's chooser and waits for a message dialog or, when
    ///     <paramref name="expectedExport" /> is given, for the file to hold it; returns the dialog's text, or null when none opened.
    /// </summary>
    private string? RunFlow(string operation, string path, string? expectedExport)
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

            PumpUntil(() => opened.OfType<MessageDialogWindow>().Any() || (expectedExport is not null && FileHolds(path, expectedExport)));
            var dialog = opened.OfType<MessageDialogWindow>().SingleOrDefault();
            var text = dialog?.FindControl<TextBlock>("MessageTextBlock")!.Text;
            dialog?.Close();
            return text;
        }
        finally
        {
            window.Close();
        }
    }

    private static bool FileHolds(string path, string expected)
    {
        try
        {
            return File.ReadAllText(path) == expected;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
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
