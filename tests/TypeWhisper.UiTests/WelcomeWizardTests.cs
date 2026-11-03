using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.Views;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class WelcomeWizardTests(UiSession ui)
{
    [Fact]
    public async Task OpensOnFirstStep()
    {
        await ui.Session.Dispatch(() =>
        {
            var vm = ui.Services.GetRequiredService<WelcomeWizardViewModel>();
            var wizard = ui.Services.GetRequiredService<WelcomeWizard>();
            wizard.DataContext = vm;
            wizard.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                wizard.UpdateLayout();

                var steps = wizard.GetVisualDescendants().OfType<TabControl>().Single();
                Assert.Equal(0, vm.StepIndex);
                Assert.Equal(0, steps.SelectedIndex);
                Assert.NotNull(steps.SelectedContent);
                Assert.True(vm.IsFirstStep);
                Assert.All(steps.Items.OfType<TabItem>(), tab => Assert.False(tab.IsEffectivelyVisible));

                vm.StepIndex = 1;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(1, steps.SelectedIndex);
                Assert.Same(((TabItem)steps.Items[1]!).Content, steps.SelectedContent);
            }
            finally
            {
                wizard.Close();
            }
        }, CancellationToken.None);
    }
}
