using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Logging;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Linux;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class LocalModelCardListTests(UiSession ui)
{
    [Fact]
    public async Task ModelCardList_FiltersCardsWithoutBindingErrors()
    {
        await ui.Session.Dispatch(() =>
        {
            var viewModel = ui.Services.GetRequiredService<MainWindowViewModel>();
            var dictation = ui.Services.GetRequiredService<DictationSectionViewModel>();
            var window = new MainWindow(viewModel) { Width = 1200, Height = 780 };
            var sink = new BindingErrorSink(Logger.Sink);
            Logger.Sink = sink;
            window.Show();
            try
            {
                viewModel.SelectedItem = viewModel.NavItems.Single(item => ReferenceEquals(item.Content, dictation));
                MainWindowRenderTests.Settle();
                AssertContainersMatchListing(window, dictation, expectHidden: true);

                dictation.ShowAllLocalModels = true;
                MainWindowRenderTests.Settle();
                AssertContainersMatchListing(window, dictation, expectHidden: false);

                dictation.ShowAllLocalModels = false;
                MainWindowRenderTests.Settle();
                AssertContainersMatchListing(window, dictation, expectHidden: true);

                Assert.True(sink.Errors.Count == 0, string.Join("\n", sink.Errors));
            }
            finally
            {
                Logger.Sink = sink.Inner;
                dictation.ShowAllLocalModels = false;
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static void AssertContainersMatchListing(Window window, DictationSectionViewModel dictation, bool expectHidden)
    {
        var list = window.GetVisualDescendants().OfType<ItemsControl>()
            .Single(control => ReferenceEquals(control.ItemsSource, dictation.LocalModelCards));
        var containers = list.GetRealizedContainers().Cast<ContentPresenter>().ToList();
        Assert.Equal(dictation.LocalModelCards.Count, containers.Count);
        foreach (var container in containers)
            Assert.Equal(((LocalModelCard)container.DataContext!).IsListed, container.IsVisible);
        Assert.Equal(expectHidden, containers.Any(container => !container.IsVisible));
    }

    private sealed class BindingErrorSink(ILogSink? inner) : ILogSink
    {
        public ILogSink? Inner { get; } = inner;

        public List<string> Errors { get; } = [];

        public bool IsEnabled(LogEventLevel level, string area) =>
            IsBindingError(level, area) || Inner?.IsEnabled(level, area) == true;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            if (IsBindingError(level, area))
                Errors.Add($"{source}: {messageTemplate}");
            Inner?.Log(level, area, source, messageTemplate);
        }

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (IsBindingError(level, area))
                Errors.Add($"{source}: {messageTemplate} [{string.Join(", ", propertyValues)}]");
            Inner?.Log(level, area, source, messageTemplate, propertyValues);
        }

        private static bool IsBindingError(LogEventLevel level, string area) =>
            area == LogArea.Binding && level >= LogEventLevel.Warning;
    }
}
