using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TypeWhisper.Linux.ViewModels;

namespace TypeWhisper.Linux.Views;

public partial class WelcomeWizard : Window
{
    public WelcomeWizard() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is WelcomeWizardViewModel vm)
            vm.RequestClose += (_, _) => Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is WelcomeWizardViewModel vm)
            vm.Cleanup();

        base.OnClosed(e);
    }

    private async void RunPasteSmokeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WelcomeWizardViewModel vm)
            return;

        PasteSmokeBox.Text = "";
        PasteSmokeBox.Focus();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var shouldCheckField = await vm.RunPasteSmokeTestAsync();
        if (!shouldCheckField)
            return;

        await Task.Delay(350);
        vm.CompletePasteSmokeTest(PasteSmokeBox.Text);
    }
}
