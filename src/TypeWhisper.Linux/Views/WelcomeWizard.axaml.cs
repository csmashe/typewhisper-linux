using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TypeWhisper.Linux.ViewModels;

namespace TypeWhisper.Linux.Views;

public partial class WelcomeWizard : Window
{
    private bool _isClosed;

    public WelcomeWizard()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is WelcomeWizardViewModel vm)
        {
            vm.RequestClose += (_, _) => Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;

        if (DataContext is WelcomeWizardViewModel vm)
        {
            vm.Cleanup();
        }

        base.OnClosed(e);
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; void return is mandated by the RoutedEventHandler/EventHandler delegate signature.
    private async void RunPasteSmokeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WelcomeWizardViewModel vm)
        {
            return;
        }

        try
        {
            PasteSmokeBox.Text = "";
            PasteSmokeBox.Focus();

            // Let the clear + focus changes settle on the UI thread before the test
            // simulates a paste, otherwise the paste can race the empty assignment.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var shouldCheckField = await vm.RunPasteSmokeTestAsync();
            if (!shouldCheckField)
            {
                return;
            }

            // Give the simulated paste time to land in the text box, then verify the
            // window/view model are still alive before reading the result back.
            await Task.Delay(350);
            if (_isClosed || !IsVisible || !ReferenceEquals(DataContext, vm))
            {
                return;
            }

            vm.CompletePasteSmokeTest(PasteSmokeBox.Text);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[WelcomeWizard] Paste smoke test failed: {ex.Message}");
        }
    }
}